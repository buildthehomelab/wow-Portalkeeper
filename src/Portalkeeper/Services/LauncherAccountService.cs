using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

public sealed record LauncherSession(string Token, string AccountName, DateTimeOffset ExpiresAt);

public enum LauncherSessionState { Valid, Expired, Banned, Unreachable }

/// <summary>A refusal from the launcher API, with the message the portal gave.</summary>
public sealed class LauncherApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Game-account login against the portal's launcher API, and the downloads that need it (the
/// client torrent and the patch torrents carry the player's tracker passkey).
/// </summary>
public sealed class LauncherAccountService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly Uri _api;

    public LauncherAccountService(Uri? api = null, HttpClient? http = null)
    {
        _api = api ?? RealmBranding.LauncherApi;
        _http = http ?? Http;
    }

    public async Task<LauncherSession> LoginAsync(string accountName, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_api, "login.php"),
            new { username = accountName, password }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<LoginBody>(cancellationToken)
            ?? throw new InvalidDataException("The login server sent an empty answer.");
        return new LauncherSession(body.Token, body.Account.Name, DateTimeOffset.FromUnixTimeSeconds(body.ExpiresAt));
    }

    /// <summary>
    /// Checks a saved login. Unreachable means the portal couldn't be asked (offline, server down):
    /// the caller keeps the saved login rather than locking the player out of a working launcher.
    /// </summary>
    public async Task<(LauncherSessionState State, LauncherSession? Session)> CheckAsync(LauncherSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = Authorized(HttpMethod.Get, "session.php", session.Token);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (LauncherSessionState.Expired, null);
            if (response.StatusCode == HttpStatusCode.Forbidden) return (LauncherSessionState.Banned, null);
            if (!response.IsSuccessStatusCode) return (LauncherSessionState.Unreachable, session);
            var body = await response.Content.ReadFromJsonAsync<SessionBody>(cancellationToken);
            return body is null
                ? (LauncherSessionState.Unreachable, session)
                : (LauncherSessionState.Valid, session with { AccountName = body.Account.Name, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(body.ExpiresAt) });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return (LauncherSessionState.Unreachable, session);
        }
    }

    public async Task LogoutAsync(LauncherSession session)
    {
        try
        {
            using var request = Authorized(HttpMethod.Post, "logout.php", session.Token);
            using var response = await _http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The token is forgotten locally either way; the server drops it when it expires.
        }
    }

    /// <summary>The realm's client torrent, rewritten for this player.</summary>
    public Task<byte[]> GetClientTorrentAsync(LauncherSession session, CancellationToken cancellationToken = default) =>
        GetBytesAsync("torrent.php?name=client", session, cancellationToken);

    /// <summary>The torrent for one patch in the realm folder.</summary>
    public Task<byte[]> GetPatchTorrentAsync(LauncherSession session, string fileName, CancellationToken cancellationToken = default) =>
        GetBytesAsync("patch-torrent.php?file=" + Uri.EscapeDataString(fileName), session, cancellationToken);

    private async Task<byte[]> GetBytesAsync(string relative, LauncherSession session, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, relative, session.Token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string relative, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<LauncherApiException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? message = null;
        try { message = (await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken))?.Error; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return new LauncherApiException(response.StatusCode, string.IsNullOrWhiteSpace(message)
            ? $"The login server answered {(int)response.StatusCode} {response.ReasonPhrase}."
            : message);
    }

    private sealed record AccountBody([property: JsonPropertyName("name")] string Name);
    private sealed record LoginBody(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] long ExpiresAt,
        [property: JsonPropertyName("account")] AccountBody Account);
    private sealed record SessionBody(
        [property: JsonPropertyName("expires_at")] long ExpiresAt,
        [property: JsonPropertyName("account")] AccountBody Account);
    private sealed record ErrorBody([property: JsonPropertyName("error")] string? Error);
}

/// <summary>
/// Keeps the launcher login between runs. On Windows the file is encrypted for the current Windows
/// user (DPAPI); on Linux it's readable by the owner only. The game password is never stored.
/// </summary>
public sealed class LauncherSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Portalkeeper launcher session");
    private readonly string _path;

    public LauncherSessionStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Portalkeeper", "launcher-session.dat");
    }

    public LauncherSession? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var bytes = File.ReadAllBytes(_path);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<LauncherSession>(bytes);
            return session is null || string.IsNullOrWhiteSpace(session.Token) ? null : session;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    public void Save(LauncherSession session)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                output.Write(bytes);
            }
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

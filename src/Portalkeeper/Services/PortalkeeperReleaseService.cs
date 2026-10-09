using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace Portalkeeper.Services;

public enum ReleaseCheckState { NotChecked, UpToDate, Available, Failed }
public sealed record ReleaseCheckResult(ReleaseCheckState State, string Message, string? Tag = null, string? Version = null);

// Release information. Downloading and installing a release is PortalkeeperUpdateService's job.
public sealed class PortalkeeperReleaseService
{
    public const string LatestReleaseEndpoint = "https://api.github.com/repos/buildthehomelab/wow-Portalkeeper/releases/latest";
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly Uri _portalVersion;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    public PortalkeeperReleaseService(HttpClient? http = null, Func<DateTimeOffset>? now = null, Uri? portalVersion = null)
    {
        _http = http ?? SharedHttp;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _portalVersion = portalVersion ?? new Uri(RealmBranding.LauncherApi, "version.php");
    }

    public static bool IsCheckDue(DateTimeOffset? last, DateTimeOffset now) =>
        last is null || now < last.Value || now - last.Value >= TimeSpan.FromHours(6);

    public static bool TryStableTag(string? tag, out string version)
    {
        version = "";
        if (tag is null || tag.Length > 128) return false;
        var match = Regex.Match(tag, @"\A[vV]?(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)\z", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        try
        {
            version = match.Groups["version"].Value;
            RealmConfigurationService.CompareVersions(version, version);
            return true;
        }
        catch (Exception) { version = ""; return false; }
    }
    public static Uri? ReleaseUri(string? tag) => TryStableTag(tag, out _)
        ? new Uri("https://github.com/buildthehomelab/wow-Portalkeeper/releases/tag/" + Uri.EscapeDataString(tag!)) : null;

    private static ReleaseCheckResult Describe(string? tag, string installed)
    {
        if (!TryStableTag(tag, out var version))
            return new(ReleaseCheckState.NotChecked, "Use CHECK FOR UPDATES to check for a newer stable release.");
        return RealmConfigurationService.CompareVersions(version, installed) > 0
            ? new(ReleaseCheckState.Available, $"Portalkeeper {version} is available.", tag, version)
            : new(ReleaseCheckState.UpToDate, "Portalkeeper is up to date.", tag, version);
    }
    private static void Persist(PortalkeeperSettings settings, Action<PortalkeeperSettings> save)
    {
        try { save(settings); }
        catch (Exception) { /* Read-only settings must not block startup or realm use. */ }
    }
    public async Task<ReleaseCheckResult> CheckAsync(PortalkeeperSettings settings, string installed,
        bool manual, Action<PortalkeeperSettings> save)
    {
        await _checkLock.WaitAsync();
        try
        {
            // Evermore fork: the portal names the newest release and can be asked every few minutes.
            // GitHub's API (60 requests an hour per address, shared by everyone on one network) is
            // only the fallback for when the portal can't be reached.
            if (await PortalTagAsync() is { } portalTag)
            {
                if (settings.LatestPortalkeeperReleaseTag != portalTag)
                {
                    settings.LatestPortalkeeperReleaseTag = portalTag;
                    Persist(settings, save);
                }
                return Describe(portalTag, installed);
            }
            var now = _now();
            if (!manual && !IsCheckDue(settings.LastPortalkeeperUpdateCheckUtc, now))
                return Describe(settings.LatestPortalkeeperReleaseTag, installed);
            // Record attempts, including failures, so an offline user is not queried every launch.
            settings.LastPortalkeeperUpdateCheckUtc = now;
            Persist(settings, save);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.UserAgent.ParseAdd("Portalkeeper/" + installed);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var release = json.RootElement;
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
                throw new InvalidOperationException("Not a stable release.");
            var tag = release.GetProperty("tag_name").GetString();
            if (!TryStableTag(tag, out _)) throw new InvalidOperationException("Invalid stable release version.");
            var result = Describe(tag, installed);
            // Never trust html_url or asset URLs supplied in the response for navigation.
            settings.LatestPortalkeeperReleaseTag = tag;
            Persist(settings, save);
            return result;
        }
        catch (Exception) { return new(ReleaseCheckState.Failed, "Unable to check for updates."); }
        finally { _checkLock.Release(); }
    }

    /// <summary>The stable release tag the portal's version.php names, or null when it can't say.</summary>
    private async Task<string?> PortalTagAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await _http.GetAsync(_portalVersion, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            // Only the tag is used; downloads still come from this repository's release and are verified there.
            var tag = json.RootElement.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return TryStableTag(tag, out _) ? tag : null;
        }
        catch (Exception) { return null; }
    }
}

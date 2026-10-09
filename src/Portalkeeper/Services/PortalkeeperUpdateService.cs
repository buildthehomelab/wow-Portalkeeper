using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Portalkeeper.Services;

/// <summary>
/// Updates an installed Windows Portalkeeper by itself (Evermore fork): downloads the new release's
/// Setup exe from this fork's GitHub releases, checks it against the SHA-256 digest GitHub publishes
/// for the asset, and runs it silently once Portalkeeper has closed. The installer reopens Portalkeeper.
///
/// Portable zips and Linux builds aren't updated in place; they keep the "update available" notice.
/// </summary>
public sealed class PortalkeeperUpdateService
{
    private const string Repository = "buildthehomelab/wow-Portalkeeper";
    private const long MaximumInstallerBytes = 500L * 1024 * 1024;
    private static readonly HttpClient SharedApi = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
    // Release downloads redirect to GitHub's file storage.
    private static readonly HttpClient SharedDownload = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly HttpClient _api;
    private readonly HttpClient _download;
    private readonly string _directory;

    public PortalkeeperUpdateService(HttpClient? api = null, HttpClient? download = null, string? directory = null)
    {
        _api = api ?? SharedApi;
        _download = download ?? SharedDownload;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portalkeeper", "updates");
    }

    /// <summary>True for a Windows install made by the Setup exe (Inno Setup leaves unins000.exe beside it).</summary>
    public static bool CanUpdateInPlace =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static string InstallerName(string version) => $"Portalkeeper-Setup-{version}.exe";

    /// <summary>
    /// Downloads and verifies the Setup exe for <paramref name="tag"/>, or returns the verified copy
    /// already downloaded. Throws when the release has no installer or no published checksum.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (!PortalkeeperReleaseService.TryStableTag(tag, out var version))
            throw new InvalidDataException("Not a stable release tag.");
        var name = InstallerName(version);
        var (digest, size) = await PublishedDigestAsync(tag, name, cancellationToken);

        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, name);
        if (File.Exists(target) && await HashAsync(target, cancellationToken) == digest)
            return target;

        // The URL is built from the fixed repository and the validated tag, never taken from the response.
        var url = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var response = await _download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != size)
                    throw new InvalidDataException("The installer download has an unexpected size.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > size) throw new InvalidDataException("The installer download is larger than published.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            if (await HashAsync(temp, cancellationToken) != digest)
                throw new InvalidDataException("The downloaded installer doesn't match its published SHA-256.");
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        foreach (var old in Directory.EnumerateFiles(_directory, "Portalkeeper-Setup-*.exe")
                     .Where(p => !string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return target;
    }

    /// <summary>Runs a verified installer silently; it closes any running Portalkeeper and reopens it when done.</summary>
    public static void StartInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        });
    }

    private async Task<(string Digest, long Size)> PublishedDigestAsync(string tag, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/tags/{Uri.EscapeDataString(tag)}");
        request.Headers.UserAgent.ParseAdd("Portalkeeper/" + RealmConfigurationService.CurrentVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Not a stable release.");
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            var size = asset.GetProperty("size").GetInt64();
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 7 + 64)
                throw new InvalidDataException("The release doesn't publish a SHA-256 for its installer.");
            if (size <= 0 || size > MaximumInstallerBytes)
                throw new InvalidDataException("The release installer has an unexpected size.");
            return (digest[7..].ToLowerInvariant(), size);
        }
        throw new InvalidDataException("The release has no Windows installer.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}

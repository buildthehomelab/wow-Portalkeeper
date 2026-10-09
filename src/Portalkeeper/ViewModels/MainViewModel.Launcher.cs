using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Vaultrona fork: game-account login, installing the client over BitTorrent, downloading patches
// through their torrents and sharing finished downloads with other players.
public sealed partial class MainViewModel
{
    private readonly LauncherAccountService _accountService = new();
    private readonly LauncherSessionStore _sessionStore = new();
    private readonly TorrentService _torrents = new();
    private readonly SemaphoreSlim _sharingLock = new(1, 1);
    private readonly Dictionary<string, byte[]> _patchTorrents = new(StringComparer.OrdinalIgnoreCase);
    private LauncherSession? _session;
    private byte[]? _clientTorrent;
    private bool _isCheckingLogin = true;
    private bool _isLoggingIn;
    private string _loginError = "";
    private bool _isInstallingClient;
    private CancellationTokenSource? _installCancel;
    private string _installStatus = "";
    private double _installPercent;
    private bool _isInstallingRequired;
    private string _sharingStatus = "";

    private static readonly int[] UploadLimitsKiB = { 512, 1024, 2048, 5120, 10240, 0 };
    public IReadOnlyList<string> UploadLimitChoices { get; } = new[] { "512 KB/s", "1 MB/s", "2 MB/s", "5 MB/s", "10 MB/s", "Unlimited" };

    private void StartLauncher()
    {
        try { RealmBranding.SeedRealmStore(); }
        catch (Exception ex) { _configurationStatus = UserErrorService.Format(ex, "The built-in realm could not be set up"); }
        _patchService.Downloader = DownloadPatchViaTorrentAsync;
        _ = RestoreLoginAsync();
        _ = RunSharingStatusLoopAsync();
    }

    // ---------------------------------------------------------
    // Login
    // ---------------------------------------------------------

    public bool IsLoggedIn => _session is not null;
    public bool ShowLogin => _session is null;
    public bool IsCheckingLogin => _isCheckingLogin;
    public bool CanLogIn => !_isLoggingIn && !_isCheckingLogin;
    public string LoginError => _loginError;
    public bool HasLoginError => _loginError.Length > 0;
    public string AccountName => _session?.AccountName ?? "";
    public string AccountSummary => _session is null ? "" : "Logged in as " + _session.AccountName;
    public string AccountSignupUrl => RealmBranding.AccountSignupUrl;
    public string LoginStatus => _isCheckingLogin ? "Checking your login..." : _isLoggingIn ? "Logging in..." : "";

    private async Task RestoreLoginAsync()
    {
        try
        {
            var saved = _sessionStore.Load();
            if (saved is null) return;
            var (state, session) = await _accountService.CheckAsync(saved);
            switch (state)
            {
                case LauncherSessionState.Valid:
                    _session = session;
                    _sessionStore.Save(session!);
                    break;
                case LauncherSessionState.Unreachable:
                    // Offline or the portal is down: keep the saved login so the launcher still works.
                    _session = saved;
                    break;
                case LauncherSessionState.Banned:
                    _sessionStore.Clear();
                    _loginError = "This account is banned.";
                    break;
                default:
                    _sessionStore.Clear();
                    _loginError = "Your login has expired. Log in again.";
                    break;
            }
        }
        catch (Exception ex) { _loginError = UserErrorService.Format(ex, "Couldn't check your login"); }
        finally
        {
            _isCheckingLogin = false;
            NotifyLoginChanged();
        }
        if (IsLoggedIn) await AfterLoginAsync();
    }

    public async Task LogInAsync(string accountName, string password)
    {
        if (!CanLogIn) return;
        if (string.IsNullOrWhiteSpace(accountName) || password.Length == 0)
        {
            _loginError = "Enter your account name and password.";
            NotifyLoginChanged();
            return;
        }
        _isLoggingIn = true;
        _loginError = "";
        NotifyLoginChanged();
        try
        {
            _session = await _accountService.LoginAsync(accountName.Trim(), password);
            try { _sessionStore.Save(_session); }
            catch (Exception) { /* Still logged in for this run; the next start asks again. */ }
        }
        catch (LauncherApiException ex) { _loginError = ex.Message; }
        catch (Exception ex) { _loginError = UserErrorService.Format(ex, "Couldn't reach the login server"); }
        finally
        {
            _isLoggingIn = false;
            NotifyLoginChanged();
        }
        if (IsLoggedIn) await AfterLoginAsync();
    }

    public async Task LogOutAsync()
    {
        var session = _session;
        _session = null;
        _clientTorrent = null;
        _patchTorrents.Clear();
        _sessionStore.Clear();
        NotifyLoginChanged();
        await ConfigureSharingAsync();
        if (session is not null) await _accountService.LogoutAsync(session);
    }

    private async Task AfterLoginAsync()
    {
        await ConfigureSharingAsync();
        await SyncRequiredAndShareAsync();
    }

    private void NotifyLoginChanged()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(IsCheckingLogin));
        OnPropertyChanged(nameof(CanLogIn));
        OnPropertyChanged(nameof(LoginError));
        OnPropertyChanged(nameof(HasLoginError));
        OnPropertyChanged(nameof(LoginStatus));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountSummary));
        NotifyInstallChanged();
        UpdateLaunchReadinessStatus();
    }

    // ---------------------------------------------------------
    // Installing the client
    // ---------------------------------------------------------

    public bool IsInstallingClient => _isInstallingClient;
    public bool CanInstallClient => IsLoggedIn && !ClientValid && !_isInstallingClient && !IsLaunching && !IsGameRunning;
    public bool HasPendingClientInstall => !string.IsNullOrWhiteSpace(_savedSettings.PendingClientInstallPath)
        && Directory.Exists(_savedSettings.PendingClientInstallPath);
    public string InstallClientButtonText => HasPendingClientInstall ? "RESUME INSTALL" : "INSTALL WOW";
    public string InstallStatus => _installStatus;
    public bool HasInstallStatus => _installStatus.Length > 0;
    public double InstallPercent => _installPercent;

    private const string InstallMarker = ".portalkeeper/client-install.json";

    /// <summary>
    /// Downloads the realm's client into a new folder (named after the torrent) inside
    /// <paramref name="parentDirectory"/>, or resumes the pending install when it's null.
    /// </summary>
    public async Task InstallClientAsync(string? parentDirectory)
    {
        if (!CanInstallClient || _session is null) return;
        _isInstallingClient = true;
        _installCancel = new CancellationTokenSource();
        var cancel = _installCancel.Token;
        SetInstallStatus("Getting the client download...", 0);
        try
        {
            _clientTorrent ??= await _accountService.GetClientTorrentAsync(_session, cancel);
            var infoHash = TorrentService.InfoHashOf(_clientTorrent);
            string target;
            if (parentDirectory is null && HasPendingClientInstall)
                target = _savedSettings.PendingClientInstallPath!;
            else if (parentDirectory is not null)
                target = Path.Combine(parentDirectory, TorrentService.NameOf(_clientTorrent));
            else
                throw new InvalidOperationException("Choose where to install World of Warcraft.");

            // Never download into a folder that holds something else, such as another WoW install.
            var marker = Path.Combine(target, InstallMarker);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()
                && !(File.Exists(marker) && File.ReadAllText(marker).Contains(infoHash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{target} already exists. Choose another location, or use LOCATE CLIENT if it's a WoW install.");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { infoHash, started = DateTimeOffset.UtcNow }), cancel);
            _savedSettings.PendingClientInstallPath = target;
            SaveSettings();

            var progress = new Progress<TorrentProgress>(p => SetInstallStatus(DescribeDownload("Downloading World of Warcraft", p), p.Percent));
            await _torrents.DownloadAsync(_clientTorrent, target, keepSharing: true, progress, cancel);

            _savedSettings.PendingClientInstallPath = null;
            _isInstallingClient = false; // lets the realm's required patches and addons install next
            SetInstallStatus("World of Warcraft is installed.", 100);
            SetClientDirectory(target);
            if (!ClientValid)
                throw new InvalidDataException("The downloaded client didn't pass validation: " + ClientStatus);
            await RediscoverRealmConfigurationAsync(); // also installs the realm's required patches and addons
        }
        catch (OperationCanceledException) { SetInstallStatus("Install paused. Click RESUME INSTALL to continue where it stopped.", _installPercent); }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.NotFound) { SetInstallStatus("The realm doesn't offer a client download yet.", 0); }
        catch (Exception ex) { SetInstallStatus(UserErrorService.Format(ex, "Install stopped"), _installPercent); }
        finally
        {
            _isInstallingClient = false;
            _installCancel?.Dispose();
            _installCancel = null;
            try { SaveSettings(); } catch (Exception) { }
            NotifyInstallChanged();
            _ = RefreshSharingAsync();
        }
    }

    public void CancelClientInstall() => _installCancel?.Cancel();

    private void SetInstallStatus(string status, double percent)
    {
        _installStatus = status;
        _installPercent = percent;
        NotifyInstallChanged();
    }

    private void NotifyInstallChanged()
    {
        OnPropertyChanged(nameof(IsInstallingClient));
        OnPropertyChanged(nameof(CanInstallClient));
        OnPropertyChanged(nameof(HasPendingClientInstall));
        OnPropertyChanged(nameof(InstallClientButtonText));
        OnPropertyChanged(nameof(InstallStatus));
        OnPropertyChanged(nameof(HasInstallStatus));
        OnPropertyChanged(nameof(InstallPercent));
        OnPropertyChanged(nameof(IsSyncingRequired));
        OnPropertyChanged(nameof(CanEnterRealm));
        OnPropertyChanged(nameof(EnterRealmButtonText));
    }

    // ---------------------------------------------------------
    // Required patches and addons: always kept installed and current
    // ---------------------------------------------------------

    public bool IsSyncingRequired => _isInstallingRequired;
    private string _syncStatus = "";

    private bool NeedsRequiredSync => IsLoggedIn && ClientValid && RealmConfigured && !IsIsolatedRealm && AddonsLoaded
        && (!PatchesReady || !AddonsReady);

    /// <summary>
    /// Installs or updates every Required patch and addon the realm lists, without asking: players
    /// only ever see the optional addons. Runs after login, when the client is set and whenever the
    /// realm configuration is refreshed (which is how a new patch version arrives).
    /// </summary>
    private async Task SyncRequiredAsync()
    {
        if (_isInstallingRequired || !NeedsRequiredSync || _isInstallingClient || _isManagingComponents
            || _isRefreshingConfiguration || IsLaunching || IsGameRunning)
            return;
        _isInstallingRequired = true;
        NotifyInstallChanged();
        try
        {
            var patches = Patches.Where(p => p.Definition.Requirement == ComponentRequirement.Required && !p.IsValid)
                .Select(p => p.Definition).ToArray();
            foreach (var patch in patches)
            {
                SetSyncStatus("Updating realm files: " + patch.Name + "...");
                await ManagePatchAsync(patch.Id, remove: false);
            }
            var addons = _addons.Where(a => a.Definition.Required && (!a.IsInstalled || a.IsUpdateAvailable))
                .Select(a => a.Definition).ToArray();
            foreach (var addon in addons)
            {
                SetSyncStatus("Updating realm addons: " + addon.Name + "...");
                await InstallOrUpdateAddonAsync(addon.Id);
            }
            _syncStatus = "";
        }
        catch (Exception ex)
        {
            _syncStatus = UserErrorService.Format(ex, "Couldn't update the realm's files; CHECK AGAIN retries");
        }
        finally
        {
            _isInstallingRequired = false;
            NotifyInstallChanged();
            UpdateLaunchReadinessStatus();
        }
    }

    private void SetSyncStatus(string status)
    {
        _syncStatus = status;
        LaunchStatus = status;
    }

    private async Task SyncRequiredAndShareAsync()
    {
        await SyncRequiredAsync();
        await RefreshSharingAsync();
    }

    // ---------------------------------------------------------
    // Optional addons: the only components players manage themselves
    // ---------------------------------------------------------

    public IReadOnlyList<AddonInfo> OptionalAddons => _addons.Where(a => !a.Definition.Required).ToArray();

    public string OptionalAddonStatus
    {
        get
        {
            if (!ClientValid) return "Install or locate World of Warcraft to add addons.";
            if (IsCheckingAddons) return "Checking addons...";
            var optional = OptionalAddons;
            if (optional.Count == 0) return AddonsLoaded ? "No extra addons are offered for this realm." : _addonStatus;
            var installed = optional.Count(a => a.IsInstalled);
            var updates = optional.Count(a => a.IsInstalled && a.IsUpdateAvailable);
            return $"{installed} of {optional.Count} addons installed" + (updates > 0 ? $" · {updates} update{(updates == 1 ? "" : "s")} available" : "") + ".";
        }
    }

    /// <summary>Updates the optional addons the player installed (never installs new ones).</summary>
    public Task UpdateInstalledAddonsAsync() => RunComponentOperationAsync(async () =>
    {
        foreach (var addon in OptionalAddons.Where(a => a.IsInstalled && a.IsUpdateAvailable).Select(a => a.Definition).ToArray())
            await _addonInstallerService.InstallOrUpdateAsync(EffectiveClientPath, addon);
        await LoadAddonsAsync();
    });

    // ---------------------------------------------------------
    // Patches over BitTorrent
    // ---------------------------------------------------------

    private async Task<byte[]> PatchTorrentAsync(string fileName, string sha256)
    {
        // One torrent per patch version: a new SHA-256 in realm.conf means a new file on the server.
        var key = fileName + "|" + sha256;
        if (_patchTorrents.TryGetValue(key, out var cached)) return cached;
        var bytes = await _accountService.GetPatchTorrentAsync(_session!, fileName);
        _patchTorrents[key] = bytes;
        return bytes;
    }

    private static readonly TimeSpan PatchStallTimeout = TimeSpan.FromSeconds(60);

    private async Task<bool> DownloadPatchViaTorrentAsync(PatchDefinition patch, string tempPath)
    {
        var fileName = RealmBranding.HostedPatchFileName(patch.SourceUrl);
        if (_session is null || fileName is null) return false;
        try
        {
            var bytes = await PatchTorrentAsync(fileName, patch.Sha256);
            var staging = Path.Combine(EffectiveClientPath, ".portalkeeper", "downloads");
            // Give up on the torrent (and let PatchService use plain HTTP) after a minute without progress.
            using var stalled = new CancellationTokenSource(PatchStallTimeout);
            long lastDone = -1;
            var progress = new Progress<TorrentProgress>(p =>
            {
                LaunchStatus = DescribeDownload("Downloading " + patch.Name, p);
                if (p.DoneBytes > lastDone)
                {
                    lastDone = p.DoneBytes;
                    stalled.CancelAfter(PatchStallTimeout);
                }
            });
            await _torrents.DownloadAsync(bytes, staging, keepSharing: false, progress, stalled.Token);
            File.Move(Path.Combine(staging, TorrentService.NameOf(bytes)), tempPath);
            return true;
        }
        catch (Exception)
        {
            return false; // PatchService falls back to the plain HTTP download.
        }
    }

    // ---------------------------------------------------------
    // Sharing with other players
    // ---------------------------------------------------------

    public bool ShareDownloads
    {
        get => _savedSettings.ShareDownloads;
        set
        {
            if (_savedSettings.ShareDownloads == value) return;
            _savedSettings.ShareDownloads = value;
            OnPropertyChanged();
            SaveSettings();
            _ = ApplySharingSettingsAsync();
        }
    }

    public int UploadLimitIndex
    {
        get
        {
            var index = Array.IndexOf(UploadLimitsKiB, _savedSettings.ShareUploadLimitKiB);
            return index < 0 ? Array.IndexOf(UploadLimitsKiB, 2048) : index;
        }
        set
        {
            if (value < 0 || value >= UploadLimitsKiB.Length || value == UploadLimitIndex) return;
            _savedSettings.ShareUploadLimitKiB = UploadLimitsKiB[value];
            OnPropertyChanged();
            SaveSettings();
            _ = ApplySharingSettingsAsync();
        }
    }

    public string SharingStatus => _sharingStatus;

    private async Task ApplySharingSettingsAsync()
    {
        await ConfigureSharingAsync();
        await RefreshSharingAsync();
    }

    private async Task ConfigureSharingAsync()
    {
        if (_savedSettings.TorrentPort is < 1024 or > 65535)
        {
            _savedSettings.TorrentPort = Random.Shared.Next(20000, 60000);
            try { SaveSettings(); } catch (Exception) { }
        }
        await _torrents.ConfigureAsync(_savedSettings.ShareDownloads && IsLoggedIn,
            _savedSettings.ShareUploadLimitKiB * 1024, _savedSettings.TorrentPort);
    }

    /// <summary>
    /// Shares the client and every verified patch that has a torrent, and stops sharing anything
    /// that's no longer current (a replaced patch, a different client folder).
    /// </summary>
    public async Task RefreshSharingAsync()
    {
        if (_session is null || IsGameRunning || _isInstallingClient) return;
        await _sharingLock.WaitAsync();
        try
        {
            var keep = new List<string>();
            if (_torrents.SharingEnabled && ClientValid && !IsIsolatedRealm)
            {
                try
                {
                    _clientTorrent ??= await _accountService.GetClientTorrentAsync(_session);
                    if (await _torrents.ShareAsync(_clientTorrent, ClientPath))
                        keep.Add(TorrentService.InfoHashOf(_clientTorrent));
                }
                catch (LauncherApiException) { /* No client torrent on this realm: nothing to share. */ }

                foreach (var patch in Patches.Where(p => p.IsValid && p.Definition.Sha256.Length > 0))
                {
                    var fileName = RealmBranding.HostedPatchFileName(patch.Definition.SourceUrl);
                    // A patch can only be shared in place when it's installed under its server name.
                    if (fileName is null || !string.Equals(Path.GetFileName(patch.Destination), fileName, StringComparison.Ordinal))
                        continue;
                    try
                    {
                        var bytes = await PatchTorrentAsync(fileName, patch.Definition.Sha256);
                        if (await _torrents.ShareAsync(bytes, Path.GetDirectoryName(patch.Destination)!))
                            keep.Add(TorrentService.InfoHashOf(bytes));
                    }
                    catch (LauncherApiException) { }
                }
            }
            await _torrents.StopSharingExceptAsync(keep);
        }
        catch (Exception ex) { _sharingStatus = UserErrorService.Format(ex, "Sharing stopped"); OnPropertyChanged(nameof(SharingStatus)); }
        finally { _sharingLock.Release(); }
    }

    private async Task RunSharingStatusLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync())
        {
            var summary = _torrents.Summary();
            var status = !IsLoggedIn ? ""
                : !_savedSettings.ShareDownloads ? "Sharing with other players is off."
                : IsGameRunning ? "Sharing paused while you play."
                : summary.Shared == 0 ? ""
                : $"Sharing {summary.Shared} download{(summary.Shared == 1 ? "" : "s")} with {summary.Peers} player{(summary.Peers == 1 ? "" : "s")} · ↑ {FormatRate(summary.UploadRate)}";
            if (status != _sharingStatus)
            {
                _sharingStatus = status;
                OnPropertyChanged(nameof(SharingStatus));
            }
        }
    }

    private async Task PauseSharingForGameAsync()
    {
        try { await _torrents.PauseAllAsync(); } catch (Exception) { }
    }

    private async Task ResumeSharingAfterGameAsync()
    {
        try { await _torrents.ResumeAllAsync(); } catch (Exception) { }
        await RefreshSharingAsync();
    }

    /// <summary>Stops every transfer and saves resume data; called as Portalkeeper closes.</summary>
    public Task ShutdownAsync() => _torrents.ShutdownAsync();

    // ---------------------------------------------------------
    // Formatting
    // ---------------------------------------------------------

    private static string DescribeDownload(string what, TorrentProgress p)
    {
        var text = $"{what}: {p.Percent:F1}% · {FormatSize(p.DoneBytes)} of {FormatSize(p.TotalBytes)} · {FormatRate(p.DownloadRate)}";
        if (p.DownloadRate > 0 && p.TotalBytes > p.DoneBytes)
        {
            var left = TimeSpan.FromSeconds((p.TotalBytes - p.DoneBytes) / (double)p.DownloadRate);
            text += left.TotalHours >= 1 ? $" · {(int)left.TotalHours} h {left.Minutes} min left" : $" · {Math.Max(1, left.Minutes)} min left";
        }
        return text + $" · {p.Peers} source{(p.Peers == 1 ? "" : "s")}";
    }

    private static string FormatSize(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";

    private static string FormatRate(long bytesPerSecond) => bytesPerSecond >= 1 << 20
        ? $"{bytesPerSecond / (double)(1 << 20):F1} MB/s"
        : $"{bytesPerSecond / 1024.0:F0} KB/s";
}

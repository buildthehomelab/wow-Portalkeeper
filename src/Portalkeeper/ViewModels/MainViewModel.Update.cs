using System;
using System.Threading;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: installed Windows builds update themselves. A newer release's installer downloads in
// the background and runs once nothing is going on (no game, no install or patch download).
public sealed partial class MainViewModel
{
    private readonly PortalkeeperUpdateService _updateService = new();
    private string? _readyInstaller;
    private string _readyVersion = "";
    private bool _isDownloadingUpdate;

    /// <summary>Raised with the verified installer's path when it's time to close and update.</summary>
    public event Action<string>? SelfUpdateReady;

    public bool CanUpdateInPlace => PortalkeeperUpdateService.CanUpdateInPlace;

    public bool AutoUpdatePortalkeeper
    {
        get => _savedSettings.AutoUpdatePortalkeeper;
        set
        {
            if (_savedSettings.AutoUpdatePortalkeeper == value) return;
            _savedSettings.AutoUpdatePortalkeeper = value;
            OnPropertyChanged();
            SaveSettings();
            if (value && _releaseTag is not null) _ = PrepareSelfUpdateAsync(_releaseTag);
        }
    }

    public bool IsUpdateReady => _readyInstaller is not null;
    public string UpdateReadyText => $"Portalkeeper {_readyVersion} is ready. It installs by itself when you're not playing or downloading.";

    private void StartSelfUpdates()
    {
        _ = RunUpdateLoopAsync();
    }

    /// <summary>Downloads the installer for a newer release (once) when this build can update itself.</summary>
    private async Task PrepareSelfUpdateAsync(string tag)
    {
        if (!CanUpdateInPlace || !_savedSettings.AutoUpdatePortalkeeper || _isDownloadingUpdate) return;
        if (!PortalkeeperReleaseService.TryStableTag(tag, out var version)) return;
        if (_readyInstaller is not null && _readyVersion == version) return;
        _isDownloadingUpdate = true;
        try
        {
            _readyInstaller = await _updateService.DownloadInstallerAsync(tag);
            _readyVersion = version;
            _updateStatus = $"Portalkeeper {version} downloaded and verified.";
        }
        catch (Exception ex)
        {
            // The notice and VIEW RELEASE still work; the next check tries again.
            _updateStatus = UserErrorService.Format(ex, "The update couldn't be downloaded");
        }
        finally
        {
            _isDownloadingUpdate = false;
            OnPropertyChanged(nameof(UpdateStatus));
            OnPropertyChanged(nameof(IsUpdateReady));
            OnPropertyChanged(nameof(UpdateReadyText));
        }
        TryApplySelfUpdate();
    }

    private bool IsIdleForUpdate => !IsGameRunning && !IsLaunching && !_isInstallingClient && !_isInstallingRequired
        && !_isManagingComponents && !_isRefreshingConfiguration && !_isLoggingIn;

    /// <summary>Starts the update now if nothing is going on. Returns false when it has to wait.</summary>
    public bool TryApplySelfUpdate(bool manual = false)
    {
        if (_readyInstaller is null || !IsIdleForUpdate) return false;
        if (!manual && _savedSettings.LastSelfUpdateVersion == _readyVersion
            && _savedSettings.LastSelfUpdateUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6))
            return false; // that install didn't take; don't keep closing the launcher to retry it
        var installer = _readyInstaller;
        _readyInstaller = null;
        _savedSettings.LastSelfUpdateVersion = _readyVersion;
        _savedSettings.LastSelfUpdateUtc = DateTimeOffset.UtcNow;
        try { SaveSettings(); } catch (Exception) { }
        LaunchStatus = $"Updating Portalkeeper to {_readyVersion}; it reopens in a moment...";
        SelfUpdateReady?.Invoke(installer);
        return true;
    }

    public void ShowUpdateWaitMessage() =>
        LaunchStatus = "The update installs as soon as the current download or game finishes.";

    private async Task RunUpdateLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var ticks = 0;
        while (await timer.WaitForNextTickAsync())
        {
            TryApplySelfUpdate();
            // Every 5 minutes: the portal answers, so a new release arrives within minutes even in a
            // launcher left open for sharing (GitHub itself is still asked at most every 6 hours).
            if (++ticks % 10 == 0) await CheckForUpdatesAsync(manual: false);
        }
    }
}

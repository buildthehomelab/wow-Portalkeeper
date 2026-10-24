using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: Legacy (LegacyService, the LEGACY page: MainViewModel.Pages.cs), laid out like the
// shop's: the LEGACY link shows once the portal has answered a listing for this login; a realm with
// Legacy off answers 404 and the link stays hidden (and goes, when a visit gets a 404). A portal that
// can't be asked, or a closed Legacy (503), is asked again after the next sync, and by the realm
// status check while the world server is up, at most every 5 minutes.
public sealed partial class MainViewModel
{
    private readonly LegacyService _legacyService = new();
    private bool _legacyOffered;
    private bool _legacyKnown;
    private LauncherSession? _legacyProbeSession;
    private DateTimeOffset _legacyRetryAfter;
    private LegacyViewModel? _legacy;

    // The portal is the built-in realm's: another realm's players have no Legacy there.
    private bool LegacyApplies => IsLoggedIn && RealmBranding.IsBuiltIn(_realmInfo);

    public bool LegacyAvailable => LegacyApplies && _legacyOffered;

    public LegacyViewModel? Legacy => _legacy;

    private bool IsLegacySaving => _legacy?.IsSaving == true;

    /// <summary>Once per login, after a sync: does this realm have Legacy? Like <see cref="ProbeShopAsync"/>.</summary>
    private async Task ProbeLegacyAsync(bool retry = false)
    {
        var session = _session;
        if (session is null || _legacyKnown || ReferenceEquals(_legacyProbeSession, session) || !LegacyApplies) return;
        if (retry && DateTimeOffset.UtcNow < _legacyRetryAfter) return;
        _legacyProbeSession = session;
        _legacyRetryAfter = DateTimeOffset.UtcNow + ShopRetryInterval;
        try
        {
            var listing = await _legacyService.GetListingAsync(session);
            if (!ReferenceEquals(session, _session)) return;
            _legacyOffered = listing is not null;
            _legacyKnown = true;
        }
        catch (LauncherApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            if (ReferenceEquals(session, _session)) _legacyOffered = false;
        }
        catch (Exception ex) when (ex is LauncherApiException or HttpRequestException or OperationCanceledException
            or JsonException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            // Closed or unreachable right now: what was known stays, and the next sync asks again.
        }
        finally
        {
            if (ReferenceEquals(_legacyProbeSession, session)) _legacyProbeSession = null;
            OnPropertyChanged(nameof(LegacyAvailable));
        }
    }

    /// <summary>Logged out: the page, its draft and its icons go.</summary>
    private void ForgetLegacy()
    {
        _legacyOffered = false;
        _legacyKnown = false;
        _legacyRetryAfter = default;
        OnPropertyChanged(nameof(LegacyAvailable));
        CloseLegacy();
        LeaveUnavailablePage();
    }

    /// <summary>The page's view model for this login, made on the first visit. False without Legacy to show.</summary>
    private bool EnsureLegacy()
    {
        if (_legacy is not null) return true;
        if (_session is not { } session || !LegacyAvailable) return false;
        var legacy = new LegacyViewModel(_legacyService, session, _realmArmoryService.LoadItemIconAsync);
        legacy.LoginExpired += message => _ = ShopLoginExpiredAsync(message);
        legacy.Unavailable += LegacyTurnedOff;
        legacy.LeaveConfirmed += page => _ = GoToAsync(page);
        legacy.PropertyChanged += LegacyPropertyChanged;
        _legacy = legacy;
        OnPropertyChanged(nameof(Legacy));
        return true;
    }

    private void LegacyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LegacyViewModel.IsSaving)) NotifyNavigation();
    }

    /// <summary>A visit got a 404: the realm turned Legacy off, so the link goes until the next login.</summary>
    private void LegacyTurnedOff()
    {
        _legacyOffered = false;
        _legacyKnown = true;
        OnPropertyChanged(nameof(LegacyAvailable));
        LeaveUnavailablePage();
    }

    private void CloseLegacy()
    {
        if (_legacy is not { } legacy) return;
        legacy.PropertyChanged -= LegacyPropertyChanged;
        legacy.Unavailable -= LegacyTurnedOff;
        _legacy = null;
        legacy.Dispose();
        OnPropertyChanged(nameof(Legacy));
        NotifyNavigation();
    }
}

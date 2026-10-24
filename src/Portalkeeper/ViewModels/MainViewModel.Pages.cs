using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

/// <summary>The pages of the launcher's one window, picked from the top bar.</summary>
public enum LauncherPage { Home, Shop, Legacy, Armory, Calendar, PatchNotes, Addons, Settings }

// Evermore fork: the launcher is one window with pages, like a game launcher. The top bar's links
// switch the page under it (HOME, SHOP, LEGACY, ARMORY, CALENDAR, PATCH NOTES, ADDONS, and the settings gear);
// the status card and ENTER REALM stay at the bottom of every page. A page's content comes from
// the portal when the page is opened: the shop keeps its view model (and listing) while the player is
// logged in, the armory and calendar ask again when their last answer is a minute old, and the patch
// notes ask every visit. A page that stops applying (the shop after a logout, the armory on a realm
// without one) sends the player home. The shop can't be left while a purchase is on its way: its
// answer (or "it may have gone through") is only shown there. LEGACY keeps its view model (and a
// draft build) like the shop; leaving it with unsaved changes asks first, and it can't be left while
// a save is on its way.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan FeedKeptFor = TimeSpan.FromMinutes(1);

    private LauncherPage _page = LauncherPage.Home;
    private ShopViewModel? _shop;
    private ArmoryViewModel? _armory;
    private string? _armoryUrlLoaded;
    private DateTimeOffset _armoryLoaded;
    private bool _armoryLoading;
    private string _armoryStatus = "";
    private CalendarViewModel? _calendar;
    private DateTimeOffset _calendarLoaded;
    private bool _calendarLoading;
    private string _calendarStatus = "";
    private PatchNotesViewModel? _patchNotes;

    public LauncherPage CurrentPage => _page;
    public bool IsHomePage => _page == LauncherPage.Home;
    public bool IsShopPage => _page == LauncherPage.Shop;
    public bool IsLegacyPage => _page == LauncherPage.Legacy;
    public bool IsArmoryPage => _page == LauncherPage.Armory;
    public bool IsCalendarPage => _page == LauncherPage.Calendar;
    public bool IsPatchNotesPage => _page == LauncherPage.PatchNotes;
    public bool IsAddonsPage => _page == LauncherPage.Addons;
    public bool IsSettingsPage => _page == LauncherPage.Settings;

    /// <summary>The patch notes come from the built-in realm's portal.</summary>
    public bool PatchNotesAvailable => HomeInfoApplies;

    /// <summary>The top bar's links work unless a purchase is on its way in the shop, or a Legacy save.</summary>
    public bool CanNavigate => !IsShopBuying && !IsLegacySaving;

    public ShopViewModel? Shop => _shop;
    public ArmoryViewModel? Armory => _armory;
    public bool IsArmoryLoading => _armoryLoading;
    public string ArmoryStatus => _armoryStatus;
    public bool HasArmoryStatus => _armory is null && _armoryStatus.Length > 0;
    public CalendarViewModel? Calendar => _calendar;
    public bool IsCalendarLoading => _calendarLoading;
    public string CalendarStatus => _calendarStatus;
    public bool HasCalendarStatus => _calendar is null && _calendarStatus.Length > 0;
    public PatchNotesViewModel PatchNotes => _patchNotes ??= new PatchNotesViewModel(_realmLive, RealmBranding.PatchNotesUrl);

    /// <summary>Shows a page and asks the portal for what it shows.</summary>
    public async Task GoToAsync(LauncherPage page)
    {
        if (!CanNavigate && page != _page) return;
        // Unsaved Legacy perks: the page asks first, and goes on with LeaveConfirmed.
        if (_page == LauncherPage.Legacy && page != LauncherPage.Legacy && _legacy is { } legacy && !legacy.AskToLeave(page)) return;
        if (!PageApplies(page) || (page == LauncherPage.Shop && !EnsureShop()) || (page == LauncherPage.Legacy && !EnsureLegacy()))
            page = LauncherPage.Home;
        SetPage(page);
        switch (page)
        {
            case LauncherPage.Shop: await _shop!.LoadAsync(); break;
            case LauncherPage.Legacy: await _legacy!.LoadAsync(keepStatus: _legacy.HasUnsavedChanges); break;
            case LauncherPage.Armory: await OpenArmoryAsync(); break;
            case LauncherPage.Calendar: await OpenCalendarAsync(); break;
            case LauncherPage.PatchNotes: await PatchNotes.LoadAsync(); break;
            case LauncherPage.Settings:
                _ = LoadArmoryAsync(); // the transmog setting's availability
                RefreshRealmChoices();
                break;
        }
    }

    public void GoHome() => SetPage(LauncherPage.Home);

    private bool PageApplies(LauncherPage page) => page switch
    {
        LauncherPage.Shop => ShopAvailable,
        LauncherPage.Legacy => LegacyAvailable,
        LauncherPage.Armory => ArmoryAvailable,
        LauncherPage.Calendar => CalendarAvailable,
        LauncherPage.PatchNotes => PatchNotesAvailable,
        LauncherPage.Addons => ClientValid,
        _ => true
    };

    private void SetPage(LauncherPage page)
    {
        if (_page == page) return;
        _page = page;
        NotifyPage();
    }

    /// <summary>After anything that can take a page away: the page that no longer applies goes home.</summary>
    private void LeaveUnavailablePage()
    {
        if (!PageApplies(_page)) SetPage(LauncherPage.Home);
    }

    private void NotifyPage()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsShopPage));
        OnPropertyChanged(nameof(IsLegacyPage));
        OnPropertyChanged(nameof(IsArmoryPage));
        OnPropertyChanged(nameof(IsCalendarPage));
        OnPropertyChanged(nameof(IsPatchNotesPage));
        OnPropertyChanged(nameof(IsAddonsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(PatchNotesAvailable));
    }

    // ---------------------------------------------------------
    // Shop
    // ---------------------------------------------------------

    private bool IsShopBuying => _shop?.IsBuying == true;

    /// <summary>The shop's view model for this login, made on the first visit. False without a shop to show.</summary>
    private bool EnsureShop()
    {
        if (_shop is not null) return true;
        if (_session is not { } session || !ShopAvailable) return false;
        var shop = new ShopViewModel(_coinShop, session);
        shop.LoginExpired += message => _ = ShopLoginExpiredAsync(message);
        shop.PropertyChanged += ShopPropertyChanged;
        _shop = shop;
        OnPropertyChanged(nameof(Shop));
        return true;
    }

    private void ShopPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShopViewModel.IsBuying)) NotifyNavigation();
    }

    /// <summary>Logged out or on another realm: the shop and its pictures go.</summary>
    private void CloseShop()
    {
        if (_shop is not { } shop) return;
        shop.PropertyChanged -= ShopPropertyChanged;
        _shop = null;
        shop.Dispose();
        OnPropertyChanged(nameof(Shop));
        NotifyNavigation();
    }

    // ---------------------------------------------------------
    // Armory
    // ---------------------------------------------------------

    private async Task OpenArmoryAsync()
    {
        var url = ArmoryUrl;
        if (_armoryLoading) return;
        if (_armory is not null && _armoryUrlLoaded == url && DateTimeOffset.UtcNow - _armoryLoaded < FeedKeptFor) return;
        _armoryLoading = true;
        OnPropertyChanged(nameof(IsArmoryLoading));
        try
        {
            var result = await LoadArmoryAsync();
            if (url != ArmoryUrl) return; // the realm changed meanwhile; that realm's page asks again
            if (result.Feed is null)
            {
                _armoryStatus = result.Status;
                if (_armoryUrlLoaded != url) ReplaceArmory(null);
            }
            else
            {
                _armoryStatus = "";
                ReplaceArmory(new ArmoryViewModel(result.Feed, url, result.Status, _realmArmoryService, ClientPath, showTransmog: ShowTransmogrifiedAppearances));
                _armoryUrlLoaded = url;
                _armoryLoaded = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _armoryLoading = false;
            OnPropertyChanged(nameof(IsArmoryLoading));
            OnPropertyChanged(nameof(ArmoryStatus));
            OnPropertyChanged(nameof(HasArmoryStatus));
        }
    }

    private void ReplaceArmory(ArmoryViewModel? armory)
    {
        var old = _armory;
        _armory = armory;
        OnPropertyChanged(nameof(Armory));
        old?.Dispose();
    }

    // ---------------------------------------------------------
    // Calendar
    // ---------------------------------------------------------

    private async Task OpenCalendarAsync()
    {
        if (_calendarLoading) return;
        if (_calendar is not null && DateTimeOffset.UtcNow - _calendarLoaded < FeedKeptFor) return;
        _calendarLoading = true;
        OnPropertyChanged(nameof(IsCalendarLoading));
        try
        {
            var result = await LoadCalendarAsync();
            if (result.Feed is null)
            {
                _calendarStatus = result.Status;
            }
            else
            {
                _calendarStatus = "";
                _calendar = new CalendarViewModel(result.Feed, result.Status);
                _calendarLoaded = DateTimeOffset.UtcNow;
                OnPropertyChanged(nameof(Calendar));
            }
        }
        finally
        {
            _calendarLoading = false;
            OnPropertyChanged(nameof(IsCalendarLoading));
            OnPropertyChanged(nameof(CalendarStatus));
            OnPropertyChanged(nameof(HasCalendarStatus));
        }
    }
}

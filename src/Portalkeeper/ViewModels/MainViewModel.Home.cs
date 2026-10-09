using Avalonia.Media;
using Portalkeeper.Models;
using Portalkeeper.Services;

namespace Portalkeeper.ViewModels;

// Evermore fork: the single-screen home view (realm status pill, one status card, ENTER REALM).
// Everything here is derived from state the rest of the view model already keeps.
public sealed partial class MainViewModel
{
    public enum HomeCard { NoClient, Installing, Syncing, Playing, Ready, Attention }

    public string LauncherTitle => RealmBranding.LauncherName.ToUpperInvariant();
    public string PatchNotesUrl => RealmBranding.PatchNotesUrl;

    public string RealmStatusText => !RealmConfigured ? "Not set up" : _realmHealthState switch
    {
        RealmHealthState.Checking => "Checking",
        RealmHealthState.Online => "Online",
        RealmHealthState.AuthOnly => "Login only",
        RealmHealthState.WorldOnly => "World only",
        RealmHealthState.Offline => "Offline",
        _ => "Unknown"
    };

    public IBrush RealmStatusBrush => Brush.Parse(!RealmConfigured ? "#8E9AA5" : _realmHealthState switch
    {
        RealmHealthState.Online => "#3DDC84",
        RealmHealthState.AuthOnly or RealmHealthState.WorldOnly => "#F2B84B",
        RealmHealthState.Offline => "#E5604D",
        _ => "#8E9AA5"
    });

    public HomeCard Card =>
        _isInstallingClient ? HomeCard.Installing
        : !ClientValid ? HomeCard.NoClient
        : IsGameRunning || IsLaunching ? HomeCard.Playing
        : _isInstallingRequired ? HomeCard.Syncing
        : CanEnterRealm ? HomeCard.Ready
        : HomeCard.Attention;

    public bool CardIsNoClient => Card == HomeCard.NoClient;
    public bool CardIsInstalling => Card == HomeCard.Installing;
    public bool CardIsReady => Card == HomeCard.Ready;
    public bool CardIsAttention => Card == HomeCard.Attention;
    public bool CardShowsBusy => Card is HomeCard.Installing or HomeCard.Syncing;

    public string CardIcon => Card switch
    {
        HomeCard.Ready => "✓",
        HomeCard.Installing or HomeCard.NoClient => "↓",
        HomeCard.Syncing => "↻",
        HomeCard.Playing => "▶",
        _ => "!"
    };

    public IBrush CardIconBrush => Brush.Parse(Card switch
    {
        HomeCard.Ready or HomeCard.Playing => "#3DDC84",
        HomeCard.Attention => "#F2B84B",
        _ => "#E8B64C"
    });

    public string CardTitle => Card switch
    {
        HomeCard.Installing => "Downloading World of Warcraft",
        HomeCard.NoClient => HasPendingClientInstall ? "Your install is paused" : "Install World of Warcraft",
        HomeCard.Syncing => "Updating realm files",
        HomeCard.Playing => "World of Warcraft is running",
        HomeCard.Ready => "Your game is up to date",
        _ => "Needs attention"
    };

    public string CardDetail => Card switch
    {
        HomeCard.Installing => InstallStatus,
        HomeCard.NoClient => HasInstallStatus ? InstallStatus
            : "Download the realm's client, or point Portalkeeper at a 3.3.5a client you already have.",
        HomeCard.Ready => "Patches and addons are installed and verified.",
        _ => LaunchStatus
    };

    public string EnterRealmSubText => Card switch
    {
        HomeCard.Ready => "Game is up to date",
        HomeCard.Playing => "Have fun out there",
        HomeCard.Syncing => "Updating realm files...",
        HomeCard.Installing => "Downloading...",
        HomeCard.NoClient => "Install the game first",
        _ => "Not ready yet"
    };

    public string FooterText
    {
        get
        {
            var text = RealmBranding.LauncherName + " · " + ApplicationVersion;
            return SharingStatus.Length > 0 ? text + " · " + SharingStatus : text;
        }
    }

    private void NotifyHome()
    {
        OnPropertyChanged(nameof(RealmStatusText));
        OnPropertyChanged(nameof(RealmStatusBrush));
        OnPropertyChanged(nameof(Card));
        OnPropertyChanged(nameof(CardIsNoClient));
        OnPropertyChanged(nameof(CardIsInstalling));
        OnPropertyChanged(nameof(CardIsReady));
        OnPropertyChanged(nameof(CardIsAttention));
        OnPropertyChanged(nameof(CardShowsBusy));
        OnPropertyChanged(nameof(CardIcon));
        OnPropertyChanged(nameof(CardIconBrush));
        OnPropertyChanged(nameof(CardTitle));
        OnPropertyChanged(nameof(CardDetail));
        OnPropertyChanged(nameof(EnterRealmSubText));
        OnPropertyChanged(nameof(FooterText));
    }
}

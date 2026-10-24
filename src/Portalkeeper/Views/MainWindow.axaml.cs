using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Portalkeeper.ViewModels;

namespace Portalkeeper.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LoadBackground();
    }

    // A branded background image, when the build ships one; otherwise the gradient scene stays.
    private void LoadBackground()
    {
        foreach (var name in new[] { "background.jpg", "background.png" })
        {
            var uri = new System.Uri("avares://Portalkeeper/Assets/Branding/" + name);
            if (!Avalonia.Platform.AssetLoader.Exists(uri)) continue;
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            BackgroundImage.Source = new Avalonia.Media.Imaging.Bitmap(stream);
            BackgroundImage.IsVisible = true;
            return;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Drag the window by its top bar (the buttons on it handle their own clicks).
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
            BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private LegacyViewModel? _closeAsked;
    private bool _closeConfirmed;

    // Unsaved Legacy perks: closing the window asks first, on the LEGACY page (leaving the page
    // already asks, so that's where a draft is). The app shutting down (a self-update) doesn't ask.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
            && DataContext is MainViewModel { IsLegacyPage: true } vm && vm.Legacy is { } legacy && !legacy.AskToClose())
        {
            e.Cancel = true;
            if (!ReferenceEquals(_closeAsked, legacy))
            {
                if (_closeAsked is not null) _closeAsked.CloseConfirmed -= CloseAfterDiscard;
                _closeAsked = legacy;
                legacy.CloseConfirmed += CloseAfterDiscard;
            }
        }
        base.OnClosing(e);
    }

    private void CloseAfterDiscard()
    {
        _closeConfirmed = true;
        Close();
    }

    // The top bar's links: the button's Tag names the page.
    private async void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Button { Tag: string name } && System.Enum.TryParse<LauncherPage>(name, out var page))
            await vm.GoToAsync(page);
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.GoToAsync(LauncherPage.Settings);
    }

    private void Website_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            OpenUrl(string.IsNullOrWhiteSpace(vm.RealmWebsite) ? vm.AccountSignupUrl : vm.RealmWebsite);
    }

    private async void WhatsNew_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.GoToAsync(LauncherPage.PatchNotes);
    }

    private void RareMap_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            OpenUrl(vm.RareMapUrl);
    }

    private async void SendCrashReport_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.SendCrashReportAsync();
    }

    private async void AlwaysSendCrashReports_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.SendCrashReportAsync(always: true);
    }

    private void DismissCrash_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.DismissCrash();
    }

    private async void RepairGame_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.DismissCrash();
        await vm.RepairGameAsync();
    }

    private static void OpenUrl(string url)
    {
        if (!System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.Exception) { /* No browser available. */ }
    }

    private async void LocateClient_Click(
        object? sender,
        RoutedEventArgs e)
    {
        var folders =
            await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "Locate World of Warcraft 3.3.5a",
                    AllowMultiple = false
                });

        var folder = folders.FirstOrDefault();

        if (folder is null)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.SetClientDirectory(folder.Path.LocalPath);
    }

    private async void RealmCheckAgain_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        viewModel.AllowClientUpdateRetry();
        await viewModel.RediscoverRealmConfigurationAsync();
    }

    private async void EnterRealm_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var hiddenForGame = false;

        await viewModel.EnterRealmAsync(() =>
        {
            if (!viewModel.HidePortalkeeperWhileGameRuns)
                return;

            hiddenForGame = true;
            Hide();
        });

        if (hiddenForGame)
        {
            Show();
            Activate();
        }
    }

    private async void LogIn_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;
        var password = PasswordBox.Text ?? "";
        PasswordBox.Text = "";
        await viewModel.LogInAsync(AccountNameBox.Text ?? "", password);
    }

    private void LoginField_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (sender == AccountNameBox)
            PasswordBox.Focus();
        else
            LogIn_Click(sender, e);
    }

    private void CreateAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;
        try { Process.Start(new ProcessStartInfo(viewModel.AccountSignupUrl) { UseShellExecute = true }); }
        catch (System.Exception) { /* No browser available; the address is on the realm's website. */ }
    }

    private async void InstallClient_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (viewModel.HasPendingClientInstall)
        {
            await viewModel.InstallClientAsync(null);
            return;
        }

        await PickInstallFolderAsync(viewModel);
    }

    private async void InstallElsewhere_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await PickInstallFolderAsync(viewModel);
    }

    private async System.Threading.Tasks.Task PickInstallFolderAsync(MainViewModel viewModel)
    {
        // Starts at the current client: picking it makes it the realm's client, downloading only what differs.
        var current = viewModel.ClientValid ? await StorageProvider.TryGetFolderFromPathAsync(viewModel.ClientPath) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose where to install World of Warcraft (a folder with WoW in it is updated in place)",
                AllowMultiple = false,
                SuggestedStartLocation = current
            });
        var folder = folders.FirstOrDefault();
        if (folder is null)
            return;

        await viewModel.InstallClientAsync(folder.Path.LocalPath);
    }

    private void CancelInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.CancelClientInstall();
    }

    private void UpdateNow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.TryApplySelfUpdate(manual: true))
            vm.ShowUpdateWaitMessage();
    }

    private void ViewRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ViewRelease();
    }
}

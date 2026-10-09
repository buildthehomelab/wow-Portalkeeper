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
    
    private async void ManageAddons_Click(
    object? sender,
    RoutedEventArgs e)
	{
	    if (DataContext is not MainViewModel viewModel)
	        return;
	
	    var window =
	        new ManageAddonsWindow
	        {
	            DataContext = viewModel
	        };
	
	    await window.ShowDialog(this);
	}
    

    private async void RealmCheckAgain_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

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

    private async void Armory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var result = await viewModel.LoadArmoryAsync();
        if (result.Feed is null)
        {
            var error = new Window
            {
                Title = "Realm Armory", Width = 520, Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock { Text = result.Status, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(24) }
            };
            await error.ShowDialog(this);
            return;
        }

        var window = new ArmoryWindow
        {
            DataContext = new ArmoryViewModel(result.Feed, viewModel.ArmoryUrl, result.Status, viewModel.ArmoryService, viewModel.ClientPath, showTransmog: viewModel.ShowTransmogrifiedAppearances)
        };
        var realmUrl = viewModel.ArmoryUrl;
        System.ComponentModel.PropertyChangedEventHandler preferenceChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ShowTransmogrifiedAppearances) && viewModel.ArmoryUrl == realmUrl)
                ((ArmoryViewModel)window.DataContext!).SetTransmogPreference(viewModel.ShowTransmogrifiedAppearances);
        };
        viewModel.PropertyChanged += preferenceChanged;
        try { await window.ShowDialog(this); }
        finally { viewModel.PropertyChanged -= preferenceChanged; }
    }

    private async void News_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var result = await viewModel.LoadNewsAsync();
        if (result.Feed is null)
        {
            var error = new Window
            {
                Title = "Realm News",
                Width = 520,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock
                {
                    Text = result.Status,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(24)
                }
            };
            await error.ShowDialog(this);
            return;
        }

        var window = new NewsWindow
        {
            DataContext = new NewsViewModel(result.Feed, result.Status)
        };
        await window.ShowDialog(this);
    }

    private async void Calendar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var result = await viewModel.LoadCalendarAsync();
        if (result.Feed is null)
        {
            var error = new Window { Title = "Realm Calendar", Width = 520, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBlock { Text = result.Status, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(24) } };
            await error.ShowDialog(this);
            return;
        }

        var window = new CalendarWindow
        {
            DataContext = new CalendarViewModel(result.Feed, result.Status)
        };
        await window.ShowDialog(this);
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
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose where to install World of Warcraft",
                AllowMultiple = false
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

    private void ViewRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ViewRelease();
    }
    private async void Settings_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        _ = viewModel.LoadArmoryAsync();
        viewModel.RefreshRealmChoices();
        var window = new SettingsWindow
        {
            DataContext = viewModel
        };

        await window.ShowDialog(this);
    }
}

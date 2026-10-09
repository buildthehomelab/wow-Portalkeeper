using System.Linq;
using Portalkeeper.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Portalkeeper.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private async void ChangeRealm_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanSwitchRealm)
            await new RealmSelectionWindow(vm).ShowDialog(this);
    }
    private async void CheckForUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.CheckForUpdatesAsync();
    }
    private void ViewRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ViewRelease();
    }
    private async void LocateClient_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate World of Warcraft 3.3.5a",
            AllowMultiple = false
        });
        if (folders.FirstOrDefault() is { } folder)
            vm.SetClientDirectory(folder.Path.LocalPath);
    }

    private async void ManageAddons_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await new ManageAddonsWindow { DataContext = vm }.ShowDialog(this);
    }

    private async void LogOut_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            Close();
            await vm.LogOutAsync();
        }
    }
    private void Close_Click(
        object? sender,
        RoutedEventArgs e)
    {
        Close();
    }

    private async void ConstructRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanConstructRuntime)
            await vm.ConstructTestRuntimeAsync();
    }

    private async void LaunchRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanLaunchConstructedRuntime)
            await vm.LaunchConstructedRuntimeAsync();
    }

    private void ForgetRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ForgetConstructedRuntime();
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Portalkeeper.ViewModels;
using Portalkeeper.Views;

namespace Portalkeeper;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            // Self-update: close normally (saving torrent state), then run the verified installer,
            // which reopens Portalkeeper.
            string? updateInstaller = null;
            viewModel.SelfUpdateReady += installer =>
            {
                updateInstaller = installer;
                desktop.Shutdown();
            };
            // Save torrent resume data and leave the swarm cleanly (bounded, so closing never hangs).
            desktop.Exit += (_, _) =>
            {
                try { System.Threading.Tasks.Task.Run(viewModel.ShutdownAsync).Wait(System.TimeSpan.FromSeconds(8)); }
                catch (System.Exception) { }
                if (updateInstaller is not null)
                {
                    try { Portalkeeper.Services.PortalkeeperUpdateService.StartInstaller(updateInstaller); }
                    catch (System.Exception) { /* The next start finds the update again. */ }
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
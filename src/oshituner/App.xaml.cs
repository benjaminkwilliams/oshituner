using System.IO;
using System.Windows;

namespace oshituner;

public partial class App : Application
{
    private async void RunBrowserSetup(bool remove)
    {
        try
        {
            var profile = new InstallProfile();
            var browserRegistration = new BrowserRegistration(profile);
            if (remove)
            {
                await DesktopTray.RequestExitAsync(profile);
                foreach (var target in BrowserRegistration.Targets)
                    if (browserRegistration.Unpair(target))
                        await BrowserDiscovery.WakeUnregisteredHostAsync(browserRegistration, target);
                Shutdown();
            }
            else
            {
                MainWindow = new BrowserIntegrationWindow(browserRegistration, installerSetup: true);
                MainWindow.Show();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!remove)
                MessageBox.Show("Could not open Browser Integration setup.", "Oshi Tuner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void RunNativeHost(string origin)
    {
        var target = BrowserRegistration.Targets.FirstOrDefault(browser => browser.Origin == origin.TrimEnd('/') + "/");
        try
        {
            Shutdown(target is null ? 1 : await BrowserDiscovery.RunNativeHostAsync(new BrowserRegistration(new InstallProfile()), target));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Shutdown(1);
        }
    }

    private async void ExitRunningDesktop() =>
        Shutdown(await DesktopTray.RequestExitAsync(new InstallProfile()) ? 0 : 1);

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow desktop) desktop.AllowSystemExit();
        base.OnSessionEnding(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--exit"])
        {
            ExitRunningDesktop();
            return;
        }
        if (e.Args is ["--browser-setup"] or ["--remove-browser-pairings"])
        {
            RunBrowserSetup(e.Args[0] == "--remove-browser-pairings");
            return;
        }

        if (e.Args.FirstOrDefault() is { } first && first.StartsWith("chrome-extension://", StringComparison.Ordinal))
        {
            RunNativeHost(first);
            return;
        }

        var autoLaunch = e.Args is [AutoLaunch.Argument];
        try
        {
            if (autoLaunch && new SettingsStore(new InstallProfile()).Read().Oshi is not { AutoLaunch: true })
            {
                Shutdown();
                return;
            }

            var desktop = new MainWindow();
            MainWindow = desktop;
            if (autoLaunch) desktop.StartInTray();
            else desktop.Show();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!autoLaunch) MessageBox.Show("Could not open this copy of Oshi Tuner. Exit any running copy from the same folder using its tray menu and check your Windows profile permissions.",
                "Oshi Tuner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace oshituner;

public partial class MainWindow
{
    private bool _checkingUpdates;

    private void StartUpdateChecks()
    {
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(interactive: false);
        _updateTimer.Start();
        _ = CheckForUpdatesAsync(interactive: false);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(interactive: true);

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_checkingUpdates || _lifetime.IsCancellationRequested) return;
        _checkingUpdates = true;
        CheckUpdates.IsEnabled = false;
        var token = _lifetime.Token;
        string? installer = null;
        var approved = false;
        try
        {
            var installed = InstallerUpdates.IsInstalled(_profile.InstallFolder);
            if (!installed && !interactive) return;
            if (installed) InstallerUpdates.DeletePastDownloads(_profile);
            CheckUpdates.Header = "Checking for updates…";
            var current = Version.Parse(AppInformation.Current.Version);
            var release = await InstallerUpdates.CheckAsync(current, token);
            token.ThrowIfCancellationRequested();
            if (release is null)
            {
                if (interactive) UpdateMessage($"You’re up to date. No newer installer is available on GitHub. You’re running Oshi Tuner {current}.");
                return;
            }

            _log.Write("Updates", $"Oshi Tuner {release.Version} is available. Running version is {current}.");
            if (!installed)
            {
                UpdateMessage($"Oshi Tuner {release.Version} is available on GitHub. In-app installation is available in copies installed with Oshi Tuner Setup.");
                return;
            }
            if (UpdateMessage($"Oshi Tuner {release.Version} is available. Download and install it now?",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            {
                _log.Write("Updates", "Update declined.");
                return;
            }

            token.ThrowIfCancellationRequested();
            approved = true;
            CheckUpdates.Header = "Downloading update…";
            _log.Write("Updates", $"Downloading installer {release.Version}.");
            installer = await InstallerUpdates.DownloadAsync(_profile, release, token);
            token.ThrowIfCancellationRequested();
            var restart = _launchOptions with { StartInTray = _hiddenToTray, StartOshiWatch = _oshi.State.Running };
            using var setup = InstallerUpdates.Start(installer, _profile, restart);
            CheckUpdates.Header = "Installing update…";
            _log.Write("Updates", $"Installer {release.Version} started. Oshi Watch will reopen {(restart.StartOshiWatch ? "running" : "paused")}.");
            await setup.WaitForExitAsync(token);
            if (setup.ExitCode != 0) throw new InvalidOperationException($"Setup exited with code {setup.ExitCode}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            _log.Write("Updates", $"Update could not complete ({exception.GetType().Name}).");
            if (interactive || approved)
                UpdateMessage("Oshi Tuner couldn’t complete the update. Try again from Help > Check for Updates.", image: MessageBoxImage.Warning);
        }
        finally
        {
            if (installer is not null && !token.IsCancellationRequested) InstallerUpdates.TryDelete(installer);
            _checkingUpdates = false;
            CheckUpdates.Header = "Check for _Updates";
            CheckUpdates.IsEnabled = true;
        }
    }

    private MessageBoxResult UpdateMessage(string message, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.Information)
    {
        var defaultButton = buttons == MessageBoxButton.OKCancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        return IsVisible
            ? MessageBox.Show(this, message, "Oshi Tuner Updates", buttons, image, defaultButton)
            : MessageBox.Show(message, "Oshi Tuner Updates", buttons, image, defaultButton);
    }
}

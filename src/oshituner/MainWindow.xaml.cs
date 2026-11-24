using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace oshituner;

public partial class MainWindow : Window
{
    private readonly InstallProfile _profile;
    private readonly FileStream _instanceLock;
    private readonly TunerSession _session;
    private readonly TunerControl _control;
    private readonly TwitchStatusView _statusView = new();
    private readonly OshiView _oshiView = new();
    private readonly OshiCoordinator _oshi;
    private readonly DockWorkspace _workspace;
    private readonly ThemeController _theme;
    private ThemeWindow? _themeWindow;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LocalControlHost? _host;
    private readonly CancellationTokenSource _lifetime = new();
    private Window? _about;
    private ClientIdWindow? _clientIdWindow;
    private BrowserIntegrationWindow? _browserIntegration;
    private readonly BrowserRegistration _browsers;
    private readonly DesktopTray _tray;
    private readonly List<Window> _hiddenWindows = [];
    private WindowState _restoreState;
    private bool _exitRequested;
    private bool _stoppingDesktop;
    private bool _hiddenToTray;
    private bool _desktopStarted;

    public MainWindow()
    {
        InitializeComponent();
        _profile = new();
        _instanceLock = _profile.Lock();
        try { _session = new(_profile, new HttpClient { Timeout = TimeSpan.FromSeconds(15) }); }
        catch { _instanceLock.Dispose(); throw; }

        _browsers = new(_profile);
        _workspace = new DockWorkspace(this, _session, ReportError);
        _workspace.Register("twitch-status", "Twitch Status", _statusView, _statusView.FocusGrid);
        _workspace.Register("oshi", "Oshi", _oshiView, _oshiView.FocusRanked);
        _oshi = _session.CreateOshiCoordinator();
        WorkspaceHost.Content = _workspace;
        _theme = new(_session, _workspace, _profile);
        _theme.Apply();
        _control = new(_session, _profile, Dispatcher, ShowAbout, () => _about?.Close(), () => _about is not null,
            ShowClientId, () => _clientIdWindow?.Close(), () => _clientIdWindow is not null,
            _browsers, ShowBrowserIntegration, () => _browserIntegration?.Close(), _workspace,
            _theme, ShowTheme, () => _themeWindow?.Close(), _oshi);
        _oshiView.SaveRequested = settings => Run(() => _control.SetOshi(settings));
        _oshiView.RunningRequested = running => Run(() => _control.SetOshiRunning(running));
        _oshi.Changed += RenderOshi;
        _session.Changed += RenderOshi;
        _statusView.RankChoices = row => _session.CanRank(new(row.Provider, row.ChannelId)) &&
            !_session.Oshi.Ranks.Any(group => group.Channels.Any(entry => entry.Channel == new OshiChannelKey(row.Provider, row.ChannelId)))
            ? _session.Oshi.Ranks : null;
        _statusView.AddOshiRequested = (row, rank) => Run(() =>
        {
            _control.AddOshiChannel(new(row.Provider, row.ChannelId), rank);
            _control.OpenPane("oshi");
        });
        RenderOshi();
        _statusView.RefreshRequested = () => Run(() => _control.Start("twitch", false));
        _statusView.IntervalChanged = minutes => Run(() => _control.SetAutoRefresh(minutes));
        _statusView.EditSortRequested = EditSort;
        _statusView.WatchRequested = WatchChannelAsync;
        _statusView.CopyLoginRequested = login => Run(() => Clipboard.SetText(login));
        _session.Changed += RenderState;
        _watchTimer.Tick += (_, _) => UpdateWatchTabs();
        _refreshTimer.Tick += (_, _) => { if (!_session.Busy) Run(() => _control.Start("twitch", false)); };
        RenderState();

        _restoreState = WindowState;
        _tray = new(Dispatcher, RestoreFromTray, ExitDesktop);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) HideToTray();
            else _restoreState = WindowState;
        };
        for (var hours = OshiSettings.DefaultCooldownHours; hours <= OshiSettings.MaxCooldownHours; hours++)
        {
            var item = new MenuItem { Header = hours == 1 ? "1 hour" : $"{hours} hours", Tag = hours, IsCheckable = true };
            item.Click += AutoLaunchCooldown_Click;
            AutoLaunchCooldown.Items.Add(item);
        }
        Closing += Window_Closing;
        _ = DesktopTray.ObserveExitAsync(_profile, ExitDesktop, _lifetime.Token);
    }

    private void RenderOshi() => _oshiView.Render(_control.GetOshi(), _session.Channels, _session.CanRank);

    private void ShowOshi_Click(object sender, RoutedEventArgs e) => Run(() => _control.OpenPane("oshi"));

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        StartDesktop(autoLaunch: false);
    }

    internal void StartInTray()
    {
        _hiddenToTray = true;
        _workspace.SetTrayHidden(true);
        StartDesktop(autoLaunch: true);
    }

    private async void StartDesktop(bool autoLaunch)
    {
        if (_desktopStarted) return;
        _desktopStarted = true;
        _workspace.Restore();
        if (autoLaunch) _oshi.SetRunning(true);
        _ = ObserveWatchChangesAsync();
        _watchTimer.Start();
        try
        {
            var host = await LocalControlHost.StartAsync(_control, _profile, _session.PreferredPort, _lifetime.Token);
            if (_lifetime.IsCancellationRequested)
            {
                await host.DisposeAsync();
                return;
            }
            _host = host;
            _session.RememberPort(host.Port);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or CryptographicException)
        {
            ControlStatus.Text = "Local API unavailable. The desktop controls are available.";
        }
        if (!_lifetime.IsCancellationRequested && !_session.Busy)
        {
            await _session.RestoreAsync();
            UpdateWatchTabs();
        }
    }

    internal void AllowSystemExit() => _exitRequested = true;

    private async void ExitDesktop()
    {
        if (_exitRequested || _stoppingDesktop) return;
        _stoppingDesktop = true;
        _oshi.SetRunning(false);
        _lifetime.Cancel();
        var host = _host;
        _host = null;
        if (host is not null)
        {
            try { await host.DisposeAsync(); }
            catch (OperationCanceledException) { }
        }
        _exitRequested = true;
        Close();
    }

    private void HideToTray()
    {
        if (_hiddenToTray || _exitRequested || _stoppingDesktop) return;
        _hiddenToTray = true;
        _workspace.SetTrayHidden(true);
        // Windows can temporarily hide owned HWNDs before WPF reports the owner as minimized.
        _hiddenWindows.AddRange(_workspace.FloatingWindows.Union(
            OwnedWindows.OfType<Window>().Where(window => window.Visibility == Visibility.Visible)));
        foreach (var window in _hiddenWindows) window.Hide();
        Hide();
    }

    private void RestoreFromTray()
    {
        if (_hiddenToTray)
        {
            WindowState = _restoreState;
            Show();
            _hiddenToTray = false;
            foreach (var window in _hiddenWindows)
                if (OwnedWindows.OfType<Window>().Contains(window)) window.Show();
            _hiddenWindows.Clear();
            _workspace.SetTrayHidden(false);
        }
        Activate();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(HideToTray);
            return;
        }

        _tray.Dispose();
        _oshi.Changed -= RenderOshi;
        _session.Changed -= RenderOshi;
        _oshi.Dispose();
        _workspace.Stop();
        _theme.Dispose();
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _watchTimer.Stop();
        var shutdown = _host?.DisposeAsync();
        if (_session.Oshi.AutoLaunch) AutoLaunch.RecordExit(_profile);
        _session.Dispose();
        _instanceLock.Dispose();
        if (shutdown is { } pending) _ = pending.AsTask();
    }

    private async Task ObserveWatchChangesAsync()
    {
        try { await _session.ObserveWatchChangesAsync(UpdateWatchTabs, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { ReportError("Could not receive browser tab changes."); }
    }

    private async void UpdateWatchTabs()
    {
        try { await _session.SyncWatchTabsAsync(_lifetime.Token); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not read the browser's Twitch tabs.");
        }
    }

    private async Task WatchChannelAsync(FollowedChannelRow row, string browser)
    {
        try { await _control.WatchChannelAsync(row.ChannelId, browser, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ReportError(exception.Message);
        }
    }

    private void AutoLaunch_Opened(object sender, RoutedEventArgs e)
    {
        AutoLaunchEnabled.IsChecked = _session.Oshi.AutoLaunch;
        foreach (var item in AutoLaunchCooldown.Items.OfType<MenuItem>())
            item.IsChecked = (int)item.Tag == _session.Oshi.AutoLaunchCooldownHours;
    }

    private void AutoLaunchEnabled_Click(object sender, RoutedEventArgs e) =>
        Run(() => _control.SetOshi(_session.Oshi with { AutoLaunch = AutoLaunchEnabled.IsChecked }));

    private void AutoLaunchCooldown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: int hours })
            Run(() => _control.SetOshi(_session.Oshi with { AutoLaunchCooldownHours = hours }));
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => _control.OpenTheme();

    private void ShowTheme()
    {
        if (_themeWindow is not null)
        {
            _themeWindow.Activate();
            return;
        }
        _themeWindow = new ThemeWindow(_theme) { Owner = this };
        _workspace.TrackDialog(_themeWindow, "theme");
        _themeWindow.Closed += (_, _) => _themeWindow = null;
        _themeWindow.Show();
    }

    private void BrowserIntegration_Click(object sender, RoutedEventArgs e) => _control.OpenBrowserIntegration();

    internal void ShowBrowserIntegration()
    {
        if (_browserIntegration is not null)
        {
            _browserIntegration.Refresh();
            _browserIntegration.Activate();
            return;
        }
        _browserIntegration = new BrowserIntegrationWindow(_browsers) { Owner = this };
        _workspace.TrackDialog(_browserIntegration, "browser-integration");
        _browserIntegration.Closed += (_, _) => _browserIntegration = null;
        _browserIntegration.Show();
    }

    private void ClientId_Click(object sender, RoutedEventArgs e) => _control.OpenClientId("twitch");

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.ClientId.Length == 0)
            _control.OpenClientId("twitch");
        else
            Run(() => _control.Start("twitch", true));
    }
    private void CancelAuthorizationButton_Click(object sender, RoutedEventArgs e) => Run(() => _control.Cancel("twitch"));
    private void DisconnectButton_Click(object sender, RoutedEventArgs e) => Run(() => _control.Disconnect("twitch"));
    private void OpenActivationButton_Click(object sender, RoutedEventArgs e) => Run(() => _control.OpenActivation("twitch"));
    private void About_Click(object sender, RoutedEventArgs e) => _control.OpenAbout();

    private void CopyCodexConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            ControlStatus.Text = "Local API unavailable.";
            return;
        }
        Run(() =>
        {
            Clipboard.SetText(_host.CodexConfiguration);
            ControlStatus.Text = "Codex configuration copied.";
        });
    }

    private void ShowTwitchStatus_Click(object sender, RoutedEventArgs e) =>
        Run(() => _control.OpenPane("twitch-status"));

    private void EditSort()
    {
        var dialog = new SortWindow(_control.GetSort()) { Owner = Window.GetWindow(_statusView) ?? this };
        _workspace.TrackDialog(dialog, "sort");
        if (dialog.ShowDialog() == true) Run(() => _control.SetSort(dialog.Selection));
    }

    private void ShowClientId()
    {
        if (_clientIdWindow is not null)
        {
            _clientIdWindow.Activate();
            return;
        }

        _clientIdWindow = new ClientIdWindow(_control, _session.ClientId, !_session.Busy && _session.UserId is null)
            { Owner = this };
        _workspace.TrackDialog(_clientIdWindow, "client-id");
        _clientIdWindow.Closed += (_, _) => _clientIdWindow = null;
        _clientIdWindow.Show();
    }

    private void ShowAbout()
    {
        if (_about is not null)
        {
            _about.Activate();
            return;
        }
        _about = new AboutWindow(_control) { Owner = this };
        _workspace.TrackDialog(_about, "about");
        _about.Closed += (_, _) => _about = null;
        _about.Show();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or ExternalException)
        {
            ReportError(ex.Message);
        }
    }

    private void ReportError(string message) => MessageText.Text = message;

    private void RenderState()
    {
        var connected = _session.UserId is not null;
        var authorizing = _session.ActivationUri is not null;
        var busy = _session.Busy;
        AuthorizationPanel.Visibility = VisibleWhen(authorizing);
        OpenActivationButton.IsEnabled = authorizing;
        AuthorizationCodeText.Text = _session.ActivationCode;
        ConnectButton.Visibility = VisibleWhen(!busy && (!connected || !_session.CanReadSubscriptions));
        ConnectButton.Content = connected ? "Enable subs" : "Connect Twitch";
        DisconnectButton.Visibility = VisibleWhen(!busy && (connected || _session.HasSavedAuthorization));
        CancelConnectButton.Visibility = VisibleWhen(busy && !authorizing);
        ConnectionStatusText.Text = authorizing ? "Approve on Twitch"
            : busy ? connected ? "Refreshing…" : "Checking Twitch…"
            : connected ? "Connected" : "Not connected";
        _clientIdWindow?.SetEditable(!busy && !connected);
        ConnectButton.IsEnabled = !busy;
        DisconnectButton.IsEnabled = !busy;
        _statusView.Render(_session);
        MessageText.Text = _session.Message;
        var interval = TimeSpan.FromMinutes(_session.AutoRefreshMinutes);
        if (_refreshTimer.Interval != interval)
        {
            _refreshTimer.Stop();
            _refreshTimer.Interval = interval;
        }
        if (connected) _refreshTimer.Start();
        else _refreshTimer.Stop();
    }

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
}

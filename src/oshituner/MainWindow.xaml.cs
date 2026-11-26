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
    private readonly LaunchLog _log;
    private readonly DockWorkspace _workspace;
    private readonly ThemeController _theme;
    private ThemeWindow? _themeWindow;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(24) };
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
    private readonly Dictionary<string, (NoticeSeverity Severity, string Message)> _notices = [];

    private readonly LocalProtocolActivity _protocolActivity = new();
    private readonly DesktopLaunchOptions _launchOptions;
    private readonly string _launchSource;

    public MainWindow() : this(new(), "Start Menu or executable") { }

    internal MainWindow(DesktopLaunchOptions options, string launchSource)
    {
        _launchOptions = options;
        _launchSource = launchSource;
        InitializeComponent();
        _profile = new();
        _instanceLock = _profile.Lock();
        _log = new(_profile);
        _log.Write("Startup", $"Oshi Tuner {AppInformation.Current.Version}, Twitch {AppInformation.Current.TwitchVersion}.");
        try
        {
            if (launchSource == "Launch Options")
            {
                var store = new SettingsStore(_profile);
                store.Save(store.Read() with { LaunchOptions = options });
            }
            _session = new(_profile, new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        }
        catch (Exception exception)
        {
            _log.Write("Startup", $"Could not load the installation settings ({exception.GetType().Name}).");
            _instanceLock.Dispose();
            throw;
        }
        _log.Write("Startup", "Installation settings loaded.");

        _browsers = new(_profile);
        _workspace = new DockWorkspace(this, _session, ReportError);
        _workspace.Register("twitch-status", "Twitch Status", _statusView, _statusView.FocusGrid);
        _workspace.Register("oshi", "Oshi", _oshiView, _oshiView.FocusRanked);
        var logView = new LogView();
        logView.Attach(_log);
        _workspace.Register("log", "Log", logView, logView.FocusEntries);
        _oshi = _session.CreateOshiCoordinator(_log);
        WorkspaceHost.Content = _workspace;
        _theme = new(_session, _workspace, _profile);
        _theme.Apply();
        _control = new(_session, _profile, Dispatcher, ShowAbout, () => _about?.Close(), () => _about is not null,
            ShowClientId, () => _clientIdWindow?.Close(), () => _clientIdWindow is not null,
            _browsers, ShowBrowserIntegration, () => _browserIntegration?.Close(), _workspace,
            ShowTwitchStatusForConnection, _theme, ShowTheme, () => _themeWindow?.Close(), _oshi, _log, _launchOptions, _launchSource, _protocolActivity);
        _oshiView.SaveRequested = settings => Run("Oshi settings", () => _control.SetOshi(settings));
        _oshiView.RunningRequested = running => Run("Oshi Watch", () => _control.SetOshiRunning(running));
        _oshi.Changed += RenderOshi;
        _session.Changed += RenderOshi;
        _statusView.RankChoices = row => _session.CanRank(new(row.Provider, row.ChannelId)) &&
            !_session.Oshi.Ranks.Any(group => group.Channels.Any(entry => entry.Channel == new OshiChannelKey(row.Provider, row.ChannelId)))
            ? _session.Oshi.Ranks : null;
        _statusView.AddOshiRequested = (row, rank) => Run("Rank channel", () =>
        {
            _control.AddOshiChannel(new(row.Provider, row.ChannelId), rank);
            _control.OpenPane("oshi");
        });
        RenderOshi();
        _statusView.ConnectRequested = () => Run("Twitch connection", () => _control.Start("twitch", true));
        _statusView.DisconnectRequested = () => Run("Twitch connection", () => _control.Disconnect("twitch"));
        _statusView.CancelAuthorizationRequested = () => Run("Twitch connection", () => _control.Cancel("twitch"));
        _statusView.OpenActivationRequested = () => Run("Twitch activation", () => _control.OpenActivation("twitch"));
        _statusView.RefreshRequested = () => Run("Twitch refresh", () => _control.Start("twitch", false));
        _statusView.IntervalChanged = minutes => Run("Refresh interval", () => _control.SetAutoRefresh(minutes));
        _statusView.EditSortRequested = EditSort;
        _statusView.WatchRequested = WatchChannelAsync;
        _statusView.CopyLoginRequested = login => Run("Copy Twitch login", () => Clipboard.SetText(login));
        _session.Changed += RenderState;
        _watchTimer.Tick += (_, _) =>
        {
            _log.Prune();
            UpdateWatchTabs();
        };
        _refreshTimer.Tick += (_, _) => { if (!_session.Busy) Run("Twitch refresh", () => _control.Start("twitch", false)); };
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
        _ = DesktopTray.ObserveExitAsync(_profile, ExitDesktop, message => _log.Write("IPC", message), _lifetime.Token);
    }

    private void RenderOshi()
    {
        var state = _control.GetOshi();
        _oshiView.Render(state, _session.Channels, _session.CanRank);
        _workspace.SetOshiWatchRunning(state.Running);
    }

    private void ShowOshi_Click(object sender, RoutedEventArgs e) => Run("Open pane", () => _control.OpenPane("oshi"));

    private void ShowLog_Click(object sender, RoutedEventArgs e) => Run("Open pane", () => _control.OpenPane("log"));

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        StartDesktop();
    }

    internal void StartInTray()
    {
        _hiddenToTray = true;
        _workspace.SetTrayHidden(true);
        StartDesktop();
    }

    private async void StartDesktop()
    {
        if (_desktopStarted) return;
        _desktopStarted = true;
        _log.Write("Startup", $"{_launchSource}, {(_launchOptions.StartInTray ? "tray" : "windowed")}. Oshi Watch: {_launchOptions.StartOshiWatch}; MCP: {_launchOptions.McpEnabled}; JSON-RPC: {_launchOptions.RpcEnabled}.");
        _workspace.Restore();
        _log.Write("Startup", "Workspace restored.");
        try
        {
            foreach (var pairing in _browsers.GetPairings())
                _log.Write("Browser", $"{pairing.DisplayName} registration: {pairing.State}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Write("Browser", $"Could not inspect browser registrations ({exception.GetType().Name}).");
        }
        if (_launchOptions.StartOshiWatch) _oshi.SetRunning(true);
        _ = ObserveWatchChangesAsync();
        _watchTimer.Start();
        _log.Write("Startup", "Browser tab monitoring started.");
        StartUpdateChecks();
        try
        {
            if (_launchOptions.McpEnabled || _launchOptions.RpcEnabled)
            {
                var host = await LocalControlHost.StartAsync(_control, _profile, _launchOptions, _protocolActivity, message => _log.Write("IPC", message), _lifetime.Token);
                if (_lifetime.IsCancellationRequested)
                {
                    await host.DisposeAsync();
                    return;
                }
                _host = host;
                _log.Write("Startup", "Local API listening on Windows named pipes.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or CryptographicException)
        {
            SetNotice("Local API", NoticeSeverity.Error, $"Local API unavailable ({exception.GetType().Name}). The desktop controls are available.");
        }
        if (!_lifetime.IsCancellationRequested && !_session.Busy)
        {
            var restoreExpected = _session.HasSavedAuthorization;
            _log.Write("Twitch", "Checking for a saved session.");
            await _session.RestoreAsync();
            if (_lifetime.IsCancellationRequested) return;
            var restoreResult = _session.UserId is not null ? $"Saved session connected. {_session.Channels.Count} channels loaded."
                : restoreExpected ? "Saved session did not connect." : "No saved session connected.";
            _log.Write("Twitch", restoreResult);
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

        _log.Write("Shutdown", "Desktop is exiting.");
        _tray.Dispose();
        _oshi.Changed -= RenderOshi;
        _session.Changed -= RenderOshi;
        _oshi.Dispose();
        _workspace.Stop();
        _theme.Dispose();
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _watchTimer.Stop();
        _updateTimer.Stop();
        var shutdown = _host?.DisposeAsync();
        if (_session.Oshi.AutoLaunch) AutoLaunch.RecordExit(_profile);
        _session.Dispose();
        _instanceLock.Dispose();
        if (shutdown is { } pending) _ = pending.AsTask();
    }

    private async Task ObserveWatchChangesAsync()
    {
        try { await _session.ObserveWatchChangesAsync(UpdateWatchTabs, message => _log.Write("IPC", message), _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { SetNotice("Browser notifications", NoticeSeverity.Warning, "Could not receive browser tab changes."); }
    }

    private async void UpdateWatchTabs()
    {
        try
        {
            await _session.SyncWatchTabsAsync(_lifetime.Token);
            await _oshi.ObserveWhilePausedAsync(_lifetime.Token);
            SetNotice("Browser tabs", NoticeSeverity.None, "");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetNotice("Browser tabs", NoticeSeverity.Warning, "Could not read the browser's Twitch tabs.");
        }
    }

    private async Task WatchChannelAsync(FollowedChannelRow row, string browser, bool existingOnly)
    {
        var source = $"Watch ({browser})";
        try
        {
            await _control.WatchChannelAsync(row.ChannelId, browser, _lifetime.Token, existingOnly);
            SetNotice(source, NoticeSeverity.None, "");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetNotice(source, NoticeSeverity.Error, exception.Message);
        }
    }

    private void AutoLaunch_Opened(object sender, RoutedEventArgs e)
    {
        AutoLaunchEnabled.IsChecked = _session.Oshi.AutoLaunch;
        foreach (var item in AutoLaunchCooldown.Items.OfType<MenuItem>())
            item.IsChecked = (int)item.Tag == _session.Oshi.AutoLaunchCooldownHours;
    }

    private void AutoLaunchEnabled_Click(object sender, RoutedEventArgs e) =>
        Run("Auto Launch", () => _control.SetOshi(_session.Oshi with { AutoLaunch = AutoLaunchEnabled.IsChecked }));

    private void AutoLaunchCooldown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: int hours })
            Run("Auto Launch", () => _control.SetOshi(_session.Oshi with { AutoLaunchCooldownHours = hours }));
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

    private void ShowTwitchStatusForConnection()
    {
        if (_hiddenToTray) RestoreFromTray();
        _control.OpenPane("twitch-status");
    }

    private void About_Click(object sender, RoutedEventArgs e) => _control.OpenAbout();

    private void CopyCodexConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (!_launchOptions.McpEnabled)
        {
            ControlStatus.Text = "MCP is disabled for this launch.";
            return;
        }
        if (_host is null)
        {
            ControlStatus.Text = "Local API unavailable.";
            return;
        }
        Run("Copy Codex configuration", () =>
        {
            Clipboard.SetText(_host.CodexConfiguration);
            ControlStatus.Text = "Codex configuration copied.";
        });
    }

    private void ShowTwitchStatus_Click(object sender, RoutedEventArgs e) =>
        Run("Open pane", () => _control.OpenPane("twitch-status"));

    private void EditSort()
    {
        var dialog = new SortWindow(_control.GetSort()) { Owner = Window.GetWindow(_statusView) ?? this };
        _workspace.TrackDialog(dialog, "sort");
        if (dialog.ShowDialog() == true) Run("Channel sort", () => _control.SetSort(dialog.Selection));
    }

    private void ShowClientId(bool connectAfterSave)
    {
        if (_clientIdWindow is not null)
        {
            if (connectAfterSave) _clientIdWindow.RequestConnectAfterSave();
            _clientIdWindow.Activate();
            return;
        }

        _clientIdWindow = new ClientIdWindow(_control, _session, connectAfterSave)
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

    private void Run(string source, Action action)
    {
        try
        {
            action();
            SetNotice(source, NoticeSeverity.None, "");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or ExternalException)
        {
            SetNotice(source, NoticeSeverity.Error, ex.Message);
        }
    }

    private void ReportError(string? message) =>
        SetNotice("Workspace", message is null ? NoticeSeverity.None : NoticeSeverity.Error, message ?? "");

    private void SetNotice(string source, NoticeSeverity severity, string message)
    {
        if (severity == NoticeSeverity.None)
        {
            if (!_notices.Remove(source)) return;
        }
        else
        {
            var notice = (severity, message);
            if (_notices.TryGetValue(source, out var existing) && existing == notice) return;
            _notices[source] = notice;
            _log.Write(source, $"{severity}: {message}");
        }

        MessageEntries.Children.Clear();
        foreach (var (owner, notice) in _notices)
        {
            var entry = new TextBlock
            {
                Text = $"{notice.Severity} · {owner}: {notice.Message}",
                TextWrapping = TextWrapping.Wrap
            };
            entry.SetResourceReference(TextBlock.ForegroundProperty,
                notice.Severity == NoticeSeverity.Error ? "ErrorForeground" : "WarningForeground");
            entry.SetValue(System.Windows.Automation.AutomationProperties.LiveSettingProperty,
                System.Windows.Automation.AutomationLiveSetting.Polite);
            MessageEntries.Children.Add(entry);
        }
        MessagePanel.Visibility = _notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderState()
    {
        var connected = _session.UserId is not null;
        _workspace.SetTwitchConnected(connected);
        _clientIdWindow?.SetEditable(!_session.Busy);
        _statusView.Render(_session);
        SetNotice("Twitch", _session.NoticeSeverity, _session.Notice);
        var interval = TimeSpan.FromMinutes(_session.AutoRefreshMinutes);
        if (_refreshTimer.Interval != interval)
        {
            _refreshTimer.Stop();
            _refreshTimer.Interval = interval;
        }
        if (connected) _refreshTimer.Start();
        else _refreshTimer.Stop();
    }

}

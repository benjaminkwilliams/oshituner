using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace oshituner;

public partial class MainWindow : Window
{
    private readonly InstallProfile _profile;
    private readonly FileStream _instanceLock;
    private readonly TunerSession _session;
    private readonly TunerControl _control;
    private readonly TwitchStatusView _statusView = new();
    private readonly DockWorkspace _workspace;
    private readonly ThemeController _theme;
    private ThemeWindow? _themeWindow;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private LocalControlHost? _host;
    private readonly CancellationTokenSource _lifetime = new();
    private Window? _about;
    private ClientIdWindow? _clientIdWindow;
    private BrowserIntegrationWindow? _browserIntegration;
    private readonly BrowserRegistration _browsers;

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
        WorkspaceHost.Content = _workspace;
        _theme = new(_session, _workspace, _profile);
        _theme.Apply();
        _control = new(_session, _profile, Dispatcher, ShowAbout, () => _about?.Close(), () => _about is not null,
            ShowClientId, () => _clientIdWindow?.Close(), () => _clientIdWindow is not null,
            _browsers, ShowBrowserIntegration, () => _browserIntegration?.Close(), _workspace,
            _theme, ShowTheme, () => _themeWindow?.Close());
        _statusView.RefreshRequested = () => Run(() => _control.Start("twitch", false));
        _statusView.IntervalChanged = minutes => Run(() => _control.SetAutoRefresh(minutes));
        _statusView.EditSortRequested = EditSort;
        _statusView.OpenChannelRequested = row => Run(() => _control.OpenChannel(row.Provider, row.ChannelId));
        _statusView.CopyLoginRequested = login => Run(() => Clipboard.SetText(login));
        _session.Changed += RenderState;
        _refreshTimer.Tick += (_, _) => { if (!_session.Busy) Run(() => _control.Start("twitch", false)); };
        RenderState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        _workspace.Restore();
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
            await _session.RestoreAsync();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _workspace.Stop();
        _theme.Dispose();
        _lifetime.Cancel();
        _refreshTimer.Stop();
        var shutdown = _host?.DisposeAsync();
        _session.Dispose();
        _instanceLock.Dispose();
        if (shutdown is { } pending) _ = pending.AsTask();
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

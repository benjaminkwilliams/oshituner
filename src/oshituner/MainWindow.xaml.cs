using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace oshituner;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new();
    private readonly TokenStore _tokenStore = new();
    private readonly FollowedChannelViewModel _viewModel = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private CancellationTokenSource? _operation;
    private Uri? _activationUri;
    private bool _connected;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(allowSignIn: false);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        ClientIdTextBox.Text = Environment.GetEnvironmentVariable("TWITCH_CLIENT_ID")?.Trim() ?? "";
        UpdateControls();

        if (_tokenStore.HasSavedAuthorization && ClientIdTextBox.Text.Length > 0)
        {
            await RefreshAsync(allowSignIn: false);
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _closing = true;
        _refreshTimer.Stop();
        _operation?.Cancel();
        _httpClient.Dispose();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(allowSignIn: true);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(allowSignIn: false);

    private void CancelAuthorizationButton_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _tokenStore.Clear();
            EndSession("Disconnected from Twitch.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageText.Text = "Could not remove the saved Twitch authorization. Check your Windows profile permissions.";
        }

        UpdateControls();
    }

    private void OpenActivationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activationUri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_activationUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            MessageText.Text = $"Open {_activationUri.AbsoluteUri} in your browser.";
        }
    }

    private async Task RefreshAsync(bool allowSignIn)
    {
        if (_operation is not null || _closing)
        {
            return;
        }

        var clientId = ClientIdTextBox.Text.Trim();
        if (clientId.Length == 0)
        {
            MessageText.Text = "Enter your Twitch application client ID.";
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        UpdateControls();
        MessageText.Text = _connected ? "Refreshing followed channels…" : "Checking Twitch authorization…";

        try
        {
            var oauth = new TwitchOAuthClient(_httpClient, clientId, _tokenStore);
            var access = await oauth.GetAccessAsync(allowSignIn ? ShowDeviceCode : null, operation.Token);
            if (access is null)
            {
                EndSession("Connect Twitch to authorize this app.");
                return;
            }

            _activationUri = null;
            UpdateControls();
            var api = new TwitchApiClient(_httpClient, clientId);
            var followed = api.GetFollowedChannelsAsync(access, operation.Token);
            var live = api.GetFollowedLiveStreamsAsync(access, operation.Token);
            await Task.WhenAll(followed, live);
            operation.Token.ThrowIfCancellationRequested();
            ShowChannels(await followed, await live);
            _connected = true;
            _refreshTimer.Start();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_closing)
            {
                MessageText.Text = "Twitch authorization canceled.";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException or OperationCanceledException)
        {
            if (!_closing)
            {
                MessageText.Text = exception is HttpRequestException or InvalidOperationException
                    ? exception.Message
                    : "Could not update Twitch. Check your connection and Windows profile permissions, then retry.";
            }
        }
        finally
        {
            _activationUri = null;
            _operation = null;
            if (!_closing)
            {
                UpdateControls();
            }
        }
    }

    private void ShowDeviceCode(Uri activationUri, string userCode)
    {
        _activationUri = activationUri;
        AuthorizationCodeText.Text = userCode;
        MessageText.Text = "Waiting for you to approve access on Twitch.";
        UpdateControls();
    }

    private void ShowChannels(IReadOnlyList<FollowedChannel> followed, IReadOnlyList<LiveStream> live)
    {
        const int liveStatusLimit = 2_000;
        var channels = followed.DistinctBy(channel => channel.Id).ToArray();
        var coveredIds = channels.OrderBy(channel => channel.FollowedAt).Take(liveStatusLimit)
            .Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var liveById = live.ToLookup(stream => stream.UserId, StringComparer.Ordinal);
        var rows = channels.Select(channel =>
        {
            var stream = liveById[channel.Id].FirstOrDefault();
            var status = stream is not null ? "LIVE" : coveredIds.Contains(channel.Id) ? "Offline" : "Unknown";
            return new FollowedChannelRow(channel.DisplayName, channel.Login, channel.FollowedAt,
                stream is not null, status,
                stream?.GameName ?? "", stream?.Title ?? "", stream?.ViewerCount ?? 0);
        }).OrderByDescending(row => row.IsLive).ThenByDescending(row => row.ViewerCount).ToArray();

        _viewModel.FollowedChannels.Clear();
        foreach (var row in rows)
        {
            _viewModel.FollowedChannels.Add(row);
        }

        FollowCountText.Text = $"{rows.Length} followed";
        LiveCountText.Text = $"{rows.Count(row => row.IsLive)} live";
        MessageText.Text = rows.Length == 0 ? "You don’t follow any channels yet." : "";
        if (channels.Length > liveStatusLimit)
        {
            MessageText.Text = "Twitch reports live status for only the first 2,000 channels you followed.";
        }
    }

    private void EndSession(string message)
    {
        _refreshTimer.Stop();
        _connected = false;
        _viewModel.FollowedChannels.Clear();
        MessageText.Text = message;
    }

    private void UpdateControls()
    {
        var busy = _operation is not null;
        AuthorizationPanel.Visibility = _activationUri is null ? Visibility.Collapsed : Visibility.Visible;
        OpenActivationButton.IsEnabled = _activationUri is not null;
        ListToolbar.Visibility = _connected && _activationUri is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = _connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.Visibility = _connected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = _connected || _tokenStore.HasSavedAuthorization ? Visibility.Visible : Visibility.Collapsed;
        ConnectionStatusText.Text = _connected ? "Connected" : busy ? "Connecting…" : "Not connected";
        ClientIdTextBox.IsEnabled = !busy && !_connected;
        ConnectButton.IsEnabled = !busy;
        DisconnectButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
    }
}

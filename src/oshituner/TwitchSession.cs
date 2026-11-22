using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace oshituner;

internal sealed partial class TunerSession
{
    private readonly HttpClient _http;
    private readonly TokenStore _tokens;
    private CancellationTokenSource? _operation;
    private bool _disposed;

    public string ClientId { get; private set; }
    public string? UserId { get; private set; }
    public bool CanReadSubscriptions { get; private set; }
    public bool Busy => _operation is not null;
    public bool HasSavedAuthorization => _tokens.HasSavedAuthorization;
    public Uri? ActivationUri { get; private set; }
    public string? ActivationCode { get; private set; }
    public string Message { get; private set; } = "Enter your Twitch Client ID under Plugins > Twitch > Client ID to get started.";

    public void SetClientId(string value)
    {
        RequireIdle();
        if (UserId is not null) throw new InvalidOperationException("Disconnect before changing the Client ID.");
        value = value.Trim();
        if (value.Length > 128) throw new ArgumentException("The Client ID must be at most 128 characters.");
        ClientId = value;
        Changed?.Invoke();
    }

    public Task RestoreAsync() => ClientId.Length > 0 && (HasSavedAuthorization || _tokens.CanAdoptLegacy)
        ? RefreshAsync(allowSignIn: false) : Task.CompletedTask;

    public Task StartRefresh(bool allowSignIn)
    {
        RequireIdle();
        if (ClientId.Length == 0) throw new InvalidOperationException("Enter your Twitch Client ID under Plugins > Twitch > Client ID.");
        return RefreshAsync(allowSignIn);
    }

    private async Task RefreshAsync(bool allowSignIn)
    {
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Message = UserId is not null ? "Refreshing channels…" : "Checking Twitch authorization…";
        Changed?.Invoke();

        try
        {
            _tokens.AdoptLegacy();
            var oauth = new TwitchOAuthClient(_http, ClientId, _tokens);
            var access = await oauth.GetAccessAsync(allowSignIn ? ShowDeviceCode : null, operation.Token);
            if (access is null)
            {
                EndSession("Connect Twitch to authorize this app.");
                return;
            }

            ActivationUri = null;
            ActivationCode = null;
            if (UserId != access.UserId) _rows = [];
            UserId = access.UserId;
            CanReadSubscriptions = access.CanReadSubscriptions;
            if (_settings.TwitchClientId != ClientId)
                SaveSettings(_settings with { TwitchClientId = ClientId });
            Changed?.Invoke();

            var api = new TwitchApiClient(_http, ClientId);
            var followed = api.GetFollowedChannelsAsync(access, operation.Token);
            var live = api.GetFollowedLiveStreamsAsync(access, operation.Token);
            await Task.WhenAll(followed, live);
            var follows = (await followed).DistinctBy(channel => channel.Id).ToArray();
            var subscriptions = access.CanReadSubscriptions
                ? await api.GetSubscriptionsAsync(access, follows, operation.Token)
                : new Dictionary<string, SubscriptionCheck>();
            operation.Token.ThrowIfCancellationRequested();
            SetChannels(follows, await live, subscriptions);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Message = "Twitch operation canceled.";
        }
        catch (OperationCanceledException)
        {
            Message = "Twitch did not respond in time. Try again.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            EndSession("Twitch rejected the session. Connect again to continue.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException)
        {
            Message = ex is HttpRequestException or InvalidOperationException
                ? ex.Message
                : "Could not update Twitch. Check your connection and Windows profile permissions, then retry.";
        }
        finally
        {
            ActivationUri = null;
            ActivationCode = null;
            _operation = null;
            if (!_disposed) Changed?.Invoke();
        }
    }

    public void Cancel()
    {
        if (_operation is null) throw new InvalidOperationException("No Twitch operation is running.");
        _operation.Cancel();
    }

    public void Disconnect()
    {
        RequireIdle();
        _tokens.Clear();
        EndSession("Disconnected from Twitch.");
        Changed?.Invoke();
    }

    private void EndSession(string message)
    {
        UserId = null;
        CanReadSubscriptions = false;
        _rows = [];
        Message = message;
    }

    private void ShowDeviceCode(Uri uri, string code)
    {
        ActivationUri = uri;
        ActivationCode = code;
        Message = "Waiting for you to approve access on Twitch.";
        Changed?.Invoke();
    }

    private void SetChannels(FollowedChannel[] follows, IReadOnlyList<LiveStream> live, Dictionary<string, SubscriptionCheck> subscriptions)
    {
        const int liveLimit = 2_000;
        var covered = follows.OrderBy(channel => channel.FollowedAt).Take(liveLimit)
            .Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var streams = live.ToLookup(stream => stream.UserId, StringComparer.Ordinal);
        var previous = _rows.ToDictionary(row => row.ChannelId,
            row => new SubscriptionCheck(row.Subbed, row.SubscriptionTier), StringComparer.Ordinal);
        _rows = Sort.Apply(follows.Select(channel =>
        {
            var stream = streams[channel.Id].FirstOrDefault();
            bool? online = stream is not null ? true : covered.Contains(channel.Id) ? false : null;
            var subscription = subscriptions.TryGetValue(channel.Id, out var known)
                ? known : CanReadSubscriptions ? previous.GetValueOrDefault(channel.Id) : default;
            return new FollowedChannelRow("twitch", channel.Id, channel.DisplayName, channel.Login, channel.FollowedAt,
                true, online, subscription.Subbed, stream?.GameName ?? "", stream?.Title ?? "", stream?.ViewerCount ?? 0,
                subscription.Tier);
        }));

        Message = follows.Length == 0 ? "You don’t follow any channels yet."
            : follows.Length > liveLimit ? "Twitch limits live status to the first 2,000 channels you followed."
            : !CanReadSubscriptions ? "Enable subs to check which followed channels you’re subbed to."
            : "Twitch provides sub status for followed channels. Other subs may be missing.";
    }

    private void RequireIdle()
    {
        if (Busy || _disposed) throw new InvalidOperationException("Wait for the current Twitch operation to finish.");
    }

    public void Dispose()
    {
        _disposed = true;
        _operation?.Cancel();
        _http.Dispose();
    }
}

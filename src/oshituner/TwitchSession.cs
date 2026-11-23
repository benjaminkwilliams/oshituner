using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace oshituner;

internal sealed partial class TunerSession
{
    private readonly HttpClient _http;
    private readonly TokenStore _tokens;
    private readonly BrowserDiscovery _browserDiscovery;
    private CancellationTokenSource? _operation;
    private bool _disposed;

    public string ClientId { get; private set; }
    public string? UserId { get; private set; }
    public bool CanReadSubscriptions { get; private set; }
    public string GiftDiscoveryStatus { get; private set; } = "not_checked";
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
            await RefreshChannelRowsAsync(api, access, follows, await live, operation.Token);
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
        GiftDiscoveryStatus = "not_checked";
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

    private async Task RefreshChannelRowsAsync(TwitchApiClient api, TwitchAccess access, FollowedChannel[] follows,
        IReadOnlyList<LiveStream> followedLive, CancellationToken token)
    {
        const int followedLiveLimit = 2_000;
        var followIds = follows.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var oldRows = _rows.ToDictionary(row => row.ChannelId, StringComparer.Ordinal);
        var subscriptions = new Dictionary<string, SubscriptionCheck>(StringComparer.Ordinal);
        var extraChannels = new List<TwitchChannel>();
        ExtraLiveStatus extraLive = new([], []);
        GiftDiscoveryStatus = access.CanReadSubscriptions ? "unavailable" : "permission_required";

        if (access.CanReadSubscriptions)
        {
            var rememberedForUser = _settings.RememberedTwitchGifts?.GetValueOrDefault(access.UserId) ?? [];
            var known = rememberedForUser.DistinctBy(channel => channel.Id).ToDictionary(channel => channel.Id, StringComparer.Ordinal);
            var discovery = await _browserDiscovery.GetGiftChannelsAsync(token);
            GiftDiscoveryStatus = discovery.Status;
            var knownLogins = known.Values.Select(channel => channel.Login)
                .Concat(follows.Select(channel => channel.Login)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var discovered = await TryGetUsersAsync(api, access, discovery.Logins.Where(login => !knownLogins.Contains(login)), true, token);
            var knownMetadata = await TryGetUsersAsync(api, access, known.Keys.Where(id => !followIds.Contains(id)), false, token);
            var current = known.Values.Concat(knownMetadata).Concat(discovered)
                .Concat(follows.Select(channel => new TwitchChannel(channel.Id, channel.Login, channel.DisplayName)))
                .GroupBy(channel => channel.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            subscriptions = await api.GetSubscriptionsAsync(access, current.Keys, token);
            foreach (var (id, channel) in current)
            {
                var check = subscriptions.GetValueOrDefault(id);
                if (check.Subbed == true && check.IsGift == true || known.ContainsKey(id))
                    known[id] = channel;

                if (!followIds.Contains(id) && (check.Subbed == true && check.IsGift == true
                    || check.Subbed is null && oldRows.TryGetValue(id, out var old) && old.Gifted == true && old.Subbed == true))
                    extraChannels.Add(channel);
            }

            extraLive = await api.GetLiveStatusAsync(access, extraChannels.Select(channel => channel.Id), token);
            token.ThrowIfCancellationRequested();
            var sortedKnown = known.Values.OrderBy(channel => channel.Id, StringComparer.Ordinal).ToArray();
            if (!rememberedForUser.SequenceEqual(sortedKnown))
            {
                var perUser = new Dictionary<string, TwitchChannel[]>(_settings.RememberedTwitchGifts ?? [], StringComparer.Ordinal)
                    { [access.UserId] = sortedKnown };
                SaveSettings(_settings with { RememberedTwitchGifts = perUser });
            }
        }

        token.ThrowIfCancellationRequested();
        var covered = follows.OrderBy(channel => channel.FollowedAt).Take(followedLiveLimit)
            .Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        covered.UnionWith(extraLive.Covered);
        var streams = followedLive.Concat(extraLive.Streams).ToLookup(stream => stream.UserId, StringComparer.Ordinal);

        FollowedChannelRow CreateRow(string id, string name, string login, DateTimeOffset? followedAt)
        {
            var stream = streams[id].FirstOrDefault();
            bool? online = stream is not null ? true : covered.Contains(id) ? false : null;
            var subscription = subscriptions.GetValueOrDefault(id);
            var stale = access.CanReadSubscriptions && subscription.Subbed is null
                && oldRows.TryGetValue(id, out var old) && old.Subbed == true;
            if (stale && oldRows.TryGetValue(id, out var previous))
                subscription = new(true, previous.SubscriptionTier, previous.Gifted);
            return new("twitch", id, name, login, followedAt, followedAt.HasValue, online, subscription.Subbed,
                stream?.GameName ?? "", stream?.Title ?? "", stream?.ViewerCount ?? 0,
                subscription.Tier, subscription.IsGift, stale);
        }

        _rows = Sort.Apply(follows.Select(channel => CreateRow(channel.Id, channel.DisplayName, channel.Login, channel.FollowedAt))
            .Concat(extraChannels.Select(channel => CreateRow(channel.Id, channel.DisplayName, channel.Login, null))));
        Message = !CanReadSubscriptions ? "Enable subs to show Twitch subscriptions."
            : follows.Length > followedLiveLimit ? "Twitch limits followed live status to the first 2,000 follows."
            : _rows.Any(row => row.SubscriptionCheckStale || row.Subbed is null || row.Online is null)
                ? "Some Twitch channel details could not be verified on this refresh."
                : _rows.Length == 0 ? "No followed channels or verified gifted subs." : "";
    }

    private async Task<IReadOnlyList<TwitchChannel>> TryGetUsersAsync(TwitchApiClient api, TwitchAccess access,
        IEnumerable<string> channels, bool byLogin, CancellationToken token)
    {
        try { return await api.GetUsersAsync(access, channels, byLogin, token); }
        catch (HttpRequestException exception) when (exception.StatusCode != HttpStatusCode.Unauthorized) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (JsonException) { }
        if (byLogin) GiftDiscoveryStatus = "partial";
        return [];
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

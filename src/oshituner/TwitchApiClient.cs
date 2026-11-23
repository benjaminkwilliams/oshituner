using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class TwitchApiClient(HttpClient httpClient, string clientId)
{
    private const int BatchSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<LiveStream>> GetFollowedLiveStreamsAsync(TwitchAccess access, CancellationToken cancellationToken) =>
        GetPagesAsync<LiveStream>("streams/followed", [("user_id", access.UserId), ("first", "100")], access, cancellationToken);

    public Task<IReadOnlyList<FollowedChannel>> GetFollowedChannelsAsync(TwitchAccess access, CancellationToken cancellationToken) =>
        GetPagesAsync<FollowedChannel>("channels/followed", [("user_id", access.UserId), ("first", "100")], access, cancellationToken);

    public async Task<IReadOnlyList<TwitchChannel>> GetUsersAsync(TwitchAccess access, IEnumerable<string> values,
        bool byLogin, CancellationToken token)
    {
        var users = new List<TwitchChannel>();
        foreach (var batch in values.Chunk(BatchSize))
            users.AddRange(await GetPagesAsync<TwitchChannel>("users", batch.Select(value => (byLogin ? "login" : "id", value)), access, token));
        return users;
    }

    public async Task<ExtraLiveStatus> GetLiveStatusAsync(TwitchAccess access, IEnumerable<string> channels, CancellationToken token)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var streams = new List<LiveStream>();
        foreach (var batch in channels.Chunk(BatchSize))
        {
            try
            {
                var query = batch.Select(id => ("user_id", id)).Append(("first", "100"));
                streams.AddRange(await GetPagesAsync<LiveStream>("streams", query, access, token));
                covered.UnionWith(batch);
            }
            catch (HttpRequestException exception) when (exception.StatusCode != HttpStatusCode.Unauthorized)
            {
                if (exception.StatusCode == HttpStatusCode.TooManyRequests) break;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (JsonException) { }
        }
        return new(streams, covered);
    }

    public async Task<Dictionary<string, SubscriptionCheck>> GetSubscriptionsAsync(
        TwitchAccess access, IEnumerable<string> channels, CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, SubscriptionCheck>(StringComparer.Ordinal);
        var throttled = 0;
        await Parallel.ForEachAsync(channels, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 4
        }, async (channel, token) =>
        {
            if (Volatile.Read(ref throttled) != 0) return;
            using var request = Request("subscriptions/user", [("user_id", access.UserId), ("broadcaster_id", channel)], access);
            try
            {
                using var response = await httpClient.SendAsync(request, token);
                if (response.IsSuccessStatusCode)
                    results[channel] = await ReadSubscriptionAsync(response.Content, token);
                else if (response.StatusCode == HttpStatusCode.NotFound)
                    results[channel] = new(false);
                else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    Interlocked.Exchange(ref throttled, 1);
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new HttpRequestException("Twitch rejected the subscription check.", null, response.StatusCode);
            }
            catch (HttpRequestException exception) when (exception.StatusCode != HttpStatusCode.Unauthorized) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        });
        return new(results, StringComparer.Ordinal);
    }

    private static async Task<SubscriptionCheck> ReadSubscriptionAsync(HttpContent content, CancellationToken token)
    {
        try
        {
            var subscription = await content.ReadFromJsonAsync<SubscriptionResponse>(JsonOptions, token);
            var entry = subscription?.Data?.FirstOrDefault();
            int? tier = entry?.Tier switch { "1000" => 1, "2000" => 2, "3000" => 3, _ => null };
            return new(true, tier, entry?.IsGift);
        }
        catch (JsonException)
        {
            return new(true);
        }
    }

    private HttpRequestMessage Request(string endpoint, IEnumerable<(string Key, string Value)> fields, TwitchAccess access)
    {
        var query = string.Join("&", fields.Select(field => field.Key + "=" + Uri.EscapeDataString(field.Value)));
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/{endpoint}?{query}");
        request.Headers.Authorization = new("Bearer", access.AccessToken);
        request.Headers.Add("Client-Id", clientId);
        return request;
    }

    private async Task<IReadOnlyList<T>> GetPagesAsync<T>(string endpoint, IEnumerable<(string Key, string Value)> fields,
        TwitchAccess access, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            using var request = Request(endpoint, cursor is null ? fields : fields.Append(("after", cursor)), access);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Twitch request for {endpoint} failed (HTTP {(int)response.StatusCode}).",
                    null, response.StatusCode);

            var page = await response.Content.ReadFromJsonAsync<HelixPage<T>>(JsonOptions, cancellationToken);
            if (page?.Data is null) throw new JsonException($"Twitch returned an invalid response for {endpoint}.");
            items.AddRange(page.Data);
            cursor = page.Pagination?.Cursor;
            if (!string.IsNullOrEmpty(cursor) && !seen.Add(cursor))
                throw new JsonException($"Twitch repeated a pagination cursor for {endpoint}.");
        }
        while (!string.IsNullOrEmpty(cursor));
        return items;
    }

    private sealed record HelixPage<T>(List<T>? Data, Pagination? Pagination);
    private sealed record SubscriptionResponse(SubscriptionEntry[]? Data);
    private sealed record SubscriptionEntry(string? Tier, [property: JsonPropertyName("is_gift")] bool? IsGift);
    private sealed record Pagination(string? Cursor);
}

internal readonly record struct SubscriptionCheck(bool? Subbed, int? Tier = null, bool? IsGift = null);

internal sealed record ExtraLiveStatus(IReadOnlyList<LiveStream> Streams, HashSet<string> Covered);

internal sealed record TwitchChannel(string Id, string Login,
    [property: JsonPropertyName("display_name")] string DisplayName);

internal sealed record LiveStream(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("game_name")] string GameName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("viewer_count")] int ViewerCount,
    string? Id = null,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyName("user_login")] string? UserLogin = null);

internal sealed record FollowedChannel(
    [property: JsonPropertyName("broadcaster_id")] string Id,
    [property: JsonPropertyName("broadcaster_login")] string Login,
    [property: JsonPropertyName("broadcaster_name")] string DisplayName,
    [property: JsonPropertyName("followed_at")] DateTimeOffset FollowedAt);

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class TwitchApiClient(HttpClient httpClient, string clientId)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<LiveStream>> GetFollowedLiveStreamsAsync(TwitchAccess access, CancellationToken cancellationToken) =>
        GetPagesAsync<LiveStream>("streams/followed", access, cancellationToken);

    public Task<IReadOnlyList<FollowedChannel>> GetFollowedChannelsAsync(TwitchAccess access, CancellationToken cancellationToken) =>
        GetPagesAsync<FollowedChannel>("channels/followed", access, cancellationToken);

    private async Task<IReadOnlyList<T>> GetPagesAsync<T>(string endpoint, TwitchAccess access, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? cursor = null;

        do
        {
            var url = $"https://api.twitch.tv/helix/{endpoint}?user_id={Uri.EscapeDataString(access.UserId)}&first=100";
            if (cursor is not null)
            {
                url += $"&after={Uri.EscapeDataString(cursor)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", access.AccessToken);
            request.Headers.Add("Client-Id", clientId);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Twitch request for {endpoint} failed (HTTP {(int)response.StatusCode}).",
                    inner: null,
                    response.StatusCode);
            }

            var page = await response.Content.ReadFromJsonAsync<HelixPage<T>>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"Twitch returned an empty response for {endpoint}.");
            items.AddRange(page.Data);
            cursor = page.Pagination?.Cursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return items;
    }

    private sealed record HelixPage<T>(
        [property: JsonPropertyName("data")] List<T> Data,
        [property: JsonPropertyName("pagination")] Pagination? Pagination);

    private sealed record Pagination([property: JsonPropertyName("cursor")] string? Cursor);
}

internal sealed record LiveStream(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("game_name")] string GameName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("viewer_count")] int ViewerCount);

internal sealed record FollowedChannel(
    [property: JsonPropertyName("broadcaster_id")] string Id,
    [property: JsonPropertyName("broadcaster_login")] string Login,
    [property: JsonPropertyName("broadcaster_name")] string DisplayName,
    [property: JsonPropertyName("followed_at")] DateTimeOffset FollowedAt);

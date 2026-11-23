using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace oshituner;

internal sealed partial class TunerSession
{
    public IOshiLiveSource TwitchOshiSource => new TwitchOshiLiveSource(this);

    private sealed class TwitchOshiLiveSource(TunerSession session) : IOshiLiveSource
    {
        public string Provider => "twitch";

        public async Task<OshiLiveChannel[]> ReadAsync(OshiChannelKey[] ranked, CancellationToken token)
        {
            var eligible = session._rows.Where(row => row.Provider == Provider &&
                (row.Followed || row.Subbed == true && !row.SubscriptionCheckStale))
                .ToDictionary(row => new OshiChannelKey(Provider, row.ChannelId));
            var channels = ranked.Where(key => key.Provider == Provider).Concat(eligible.Keys).Distinct().ToArray();
            if (channels.Length == 0) return [];

            var access = session._watchAccess;
            ExtraLiveStatus result = new([], []);
            if (!session._disposed && access is not null)
            {
                try
                {
                    var api = new TwitchApiClient(session._http, session.ClientId);
                    result = await api.GetLiveStatusAsync(access, channels.Select(key => key.Id), token);
                }
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (!session._disposed && !session.Busy && ReferenceEquals(access, session._watchAccess))
                        _ = session.StartRefresh(allowSignIn: false);
                }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                if (!ReferenceEquals(access, session._watchAccess)) result = new([], []);
            }

            var streams = result.Streams.ToLookup(stream => stream.UserId, StringComparer.Ordinal);
            return channels.Select(key =>
            {
                var stream = streams[key.Id].FirstOrDefault();
                var row = eligible.GetValueOrDefault(key);
                bool? online = stream is not null ? true : result.Covered.Contains(key.Id) ? false : null;
                return new OshiLiveChannel(key, stream?.UserLogin ?? row?.Login, online, row is not null,
                    stream?.Id, stream?.StartedAt);
            }).ToArray();
        }
    }
}

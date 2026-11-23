namespace oshituner;

internal sealed record OshiLiveChannel(OshiChannelKey Channel, string? Login, bool? Online,
    bool FallbackEligible, string? StreamId, DateTimeOffset? StartedAt);

internal interface IOshiLiveSource
{
    string Provider { get; }
    Task<OshiLiveChannel[]> ReadAsync(OshiChannelKey[] ranked, CancellationToken token);
}

namespace oshituner;

internal sealed record OshiTurn(string Revision, string? StreamId, DateTimeOffset Since);
internal sealed record OshiRankSkip(string Channel, string Rank, string CompetingChannel, string CompetingRank, string Reason);
internal sealed record OshiDecision(string? Action, OshiLiveChannel? Channel, OshiBrowserTab? Tab, string Reason,
    DateTimeOffset? Next = null, DateTimeOffset? FallbackSince = null, OshiRankSkip[]? RankSkips = null);

internal static class OshiPlanner
{
    public static OshiDecision Plan(OshiSettings settings, OshiBrowserSnapshot browser, OshiLiveChannel[] channels,
        IReadOnlyDictionary<int, OshiTurn> turns, IReadOnlyDictionary<(OshiChannelKey, string?), DateTimeOffset> served,
        IReadOnlyDictionary<string, DateTimeOffset> deferred, DateTimeOffset? fallbackSince, DateTimeOffset now,
        IReadOnlySet<string>? watchingElsewhere = null)
    {
        if (browser.Status != "ready") return new(null, null, null, browser.Status);

        var rank = settings.Ranks.SelectMany((group, index) =>
            group.Channels.Where(entry => settings.BrowserFor(entry) == browser.Browser)
                .Select(entry => (entry.Channel, index)))
            .ToDictionary(entry => entry.Channel, entry => entry.index);
        var memberOrder = settings.Ranks.SelectMany(group => group.Channels.Select((entry, index) => (entry.Channel, index)))
            .ToDictionary(entry => entry.Channel, entry => entry.index);
        var anyRank = memberOrder.Keys.ToHashSet();
        var timings = (settings.Timings ?? []).ToDictionary(rule => rule.Channel);
        int Priority(OshiLiveChannel? channel) => channel is not null ? rank.GetValueOrDefault(channel.Channel, int.MaxValue) : int.MaxValue;
        // The current browser protocol reports Twitch routes. Add provider-qualified routes when the bridge supports another site.
        channels = channels.Where(channel => channel.Channel.Provider == "twitch").ToArray();
        var byChannel = channels.ToDictionary(channel => channel.Channel);
        var byLogin = channels.Where(channel => !string.IsNullOrWhiteSpace(channel.Login))
            .ToLookup(channel => channel.Login!, StringComparer.OrdinalIgnoreCase);
        var slots = browser.Tabs.Select(tab =>
        {
            var channel = tab.Channel is { } key ? byChannel.GetValueOrDefault(key) : null;
            channel ??= byLogin[tab.Login].OrderBy(Priority).FirstOrDefault();
            var turn = turns[tab.TabId];
            var priority = tab.Channel is { } owned ? rank.GetValueOrDefault(owned, int.MaxValue) : Priority(channel);
            var keyForTiming = tab.Channel ?? channel?.Channel;
            var minimumWatch = keyForTiming is { } keyValue ? timings.GetValueOrDefault(keyValue)?.RotationMinutes : null;
            return (Tab: tab, Channel: channel, Rank: priority, Since: turn.Since,
                MinimumWatchUntil: turn.Since.AddMinutes(minimumWatch ?? OshiSettings.DefaultWatchMinutes));
        }).OrderByDescending(slot => slot.Channel?.Online == false)
            .ThenByDescending(slot => slot.Rank).ThenBy(slot => slot.Since).ToArray();

        var cap = settings.CapFor(browser.Browser);
        if (slots.Length > cap) return new("close", null, slots[0].Tab, "Apply browser tab limit");
        if (cap == 0) return new(null, null, null, "Browser disabled");

        var open = browser.Logins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = channels.Where(channel => channel.Online == true && channel.Login is { Length: > 0 } login
            && !open.Contains(login) && (!deferred.TryGetValue(login, out var until) || until <= now)).ToArray();
        var ranked = candidates.Where(channel => rank.ContainsKey(channel.Channel)).OrderBy(Priority)
            .ThenBy(channel => channel.StartedAt ?? channel.FirstObservedLive ?? DateTimeOffset.MaxValue)
            .ThenBy(channel => memberOrder[channel.Channel])
            .ThenBy(channel => channel.Channel.Provider, StringComparer.Ordinal)
            .ThenBy(channel => channel.Channel.Id, StringComparer.Ordinal);
        DateTimeOffset? next = null;
        var rankSkips = new List<OshiRankSkip>();
        foreach (var channel in ranked)
        {
            if (slots.Length < cap) return new("open", channel, null, "Ranked channel is live");
            var priority = Priority(channel);
            var replace = slots.FirstOrDefault(slot => slot.Channel?.Online == false || slot.Rank > priority);
            if (replace.Tab is not null) return new("switch", channel, replace.Tab, "Ranked channel takes priority");
            var wentLive = channel.StartedAt ?? channel.FirstObservedLive;
            var peer = slots.Where(slot => slot.Rank == priority && slot.Channel?.Online == true && wentLive > slot.Since)
                .OrderBy(slot => slot.MinimumWatchUntil).FirstOrDefault();
            if (peer.Tab is null)
            {
                var blocker = slots[0];
                var reason = blocker.Rank < priority ? "it has higher rank" : "the same-rank channel did not go live during this watch";
                rankSkips.Add(new(channel.Login!, settings.Ranks[priority].GridName(priority), blocker.Tab.Login,
                    settings.Ranks[blocker.Rank].GridName(blocker.Rank), reason));
                continue;
            }
            var due = peer.MinimumWatchUntil;
            if (due <= now) return new("switch", channel, peer.Tab, "Same-rank channel went live");
            rankSkips.Add(new(channel.Login!, settings.Ranks[priority].GridName(priority), peer.Tab.Login,
                settings.Ranks[priority].GridName(priority), "minimum watch time has not elapsed"));
            if (next is null || due < next) next = due;
        }

        var watchingRanked = slots.Any(slot => slot.Rank < int.MaxValue && slot.Channel?.Online != false) ||
            channels.Any(channel => rank.ContainsKey(channel.Channel) && channel.Online != false &&
            channel.Login is { } login && open.Contains(login));
        if (watchingRanked)
            return new(null, null, null, next.HasValue ? "Waiting for minimum watch time" : "Keep current tabs", next, RankSkips: rankSkips.ToArray());

        var fallback = candidates.Where(channel => channel.FallbackEligible && !anyRank.Contains(channel.Channel) &&
            !(watchingElsewhere?.Contains(channel.Login!) ?? false))
            .OrderBy(channel => served.GetValueOrDefault((channel.Channel, channel.StreamId), DateTimeOffset.MinValue))
            .ThenBy(channel => channel.StartedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(channel => channel.Channel.Id, StringComparer.Ordinal).FirstOrDefault();
        if (fallback is null) return new(null, null, null, "No eligible channel waiting", next);
        var opening = slots.Length < cap;
        var fallbackSlot = slots.FirstOrDefault(slot => slot.Rank == int.MaxValue || slot.Channel?.Online == false);
        var timedChannel = opening ? fallback.Channel : fallbackSlot.Tab?.Channel ?? fallbackSlot.Channel?.Channel ?? fallback.Channel;
        var delay = timings.GetValueOrDefault(timedChannel)?.FallbackMinutes ?? OshiSettings.DefaultFallbackMinutes;
        var fallbackDue = (fallbackSince ?? now).AddMinutes(delay);
        if (fallbackDue > now)
            return new(null, null, null, "Wait for fallback", fallbackDue, fallbackSince ?? now);
        if (opening) return new("open", fallback, null, "Watch an unranked follow or sub", FallbackSince: fallbackSince);
        return fallbackSlot.Tab is null ? new(null, null, null, "Keep current tabs")
            : new("switch", fallback, fallbackSlot.Tab, "Rotate unranked follow or sub", FallbackSince: fallbackSince);
    }
}

using System.Windows.Threading;

namespace oshituner;

internal sealed record OshiBrowserState(string Browser, string Status, string Reason, DateTimeOffset? Next,
    OshiBrowserTab[] Tabs);
internal sealed record OshiState(OshiSettings Settings, bool Running, OshiBrowserState[] Browsers);

internal sealed class OshiCoordinator(TunerSession session, BrowserDiscovery discovery, IOshiLiveSource[] sources) : IDisposable
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DegradedStatusInterval = TimeSpan.FromMinutes(1);
    private readonly DateTimeOffset _clockOrigin = TimeProvider.System.GetUtcNow();
    private readonly long _timestampOrigin = TimeProvider.System.GetTimestamp();
    private DateTimeOffset Now => _clockOrigin + TimeProvider.System.GetElapsedTime(_timestampOrigin);
    private static readonly TimeSpan ManualCloseDelay = TimeSpan.FromMinutes(10);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, BrowserRun> _runs = new(StringComparer.Ordinal);
    private OshiLiveChannel[] _live = [];
    private DateTimeOffset _statusDue;
    private OshiSettings? _statusSettings;
    private CancellationTokenSource? _run;
    private bool _busy;

    public event Action? Changed;
    public OshiState State => new(session.Oshi, _run is not null, _runs.Values.Select(run => run.State).ToArray());

    public void SetRunning(bool running)
    {
        if (running == (_run is not null)) return;
        if (running)
        {
            foreach (var browser in _runs.Keys.ToArray())
                _runs[browser] = new(browser, _runs[browser].Deferred);
            _statusSettings = null;
            _statusDue = DateTimeOffset.MinValue;
            _timer.Tick += Tick;
            _run = new();
            _timer.Start();
            Tick(this, EventArgs.Empty);
        }
        else
        {
            _timer.Stop();
            _timer.Tick -= Tick;
            var previous = _run!;
            _run = null;
            previous.Cancel();
            previous.Dispose();
        }
        Changed?.Invoke();
    }

    private async void Tick(object? sender, EventArgs args)
    {
        if (_busy || _run is null) return;
        _busy = true;
        var token = _run.Token;
        try
        {
            var now = Now;
            var settings = session.Oshi;
            if (now >= _statusDue || !Equals(settings, _statusSettings))
            {
                var keys = settings.Ranks.SelectMany(group => group.Channels).Select(entry => entry.Channel).ToArray();
                var snapshots = await Task.WhenAll(sources.Select(source => ReadSourceAsync(source, keys, token)));
                var previous = _live.ToDictionary(channel => channel.Channel);
                _live = snapshots.SelectMany(items => items).Select(channel =>
                    channel.Online is null && previous.TryGetValue(channel.Channel, out var seen)
                        ? channel with { Login = channel.Login ?? seen.Login, StreamId = seen.StreamId }
                        : channel).ToArray();
                _statusSettings = settings;
                var degraded = _live.Length != 0 && _live.All(channel => channel.Online is null);
                _statusDue = Now + (degraded ? DegradedStatusInterval : StatusInterval);
            }
            token.ThrowIfCancellationRequested();
            if (!Equals(settings, session.Oshi)) return;
            if (session.UserId is null) _live = _live.Where(channel => channel.Channel.Provider != "twitch").ToArray();
            var account = session.UserId;
            var browsers = await discovery.GetOshiTabsAsync(token);
            token.ThrowIfCancellationRequested();
            if (account != session.UserId) return;
            now = Now;
            if (!Equals(settings, session.Oshi)) return;
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var browser in browsers.OrderBy(browser => browser.Browser != settings.DefaultBrowser))
            {
                if (!_runs.TryGetValue(browser.Browser, out var run))
                    _runs.Add(browser.Browser, run = new(browser.Browser));
                if (browser.Status == "ready") run.Observe(browser, _live, now);
                var watchingElsewhere = browsers.Where(other => other.Browser != browser.Browser)
                    .SelectMany(other => other.Logins).Concat(reserved).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var decision = OshiPlanner.Plan(settings, browser, _live, run.Turns, run.Served, run.Deferred,
                    run.FallbackSince, now, watchingElsewhere);
                run.FallbackSince = decision.FallbackSince;
                var next = decision.Next is { } due ? DateTimeOffset.UtcNow + (due - now) : (DateTimeOffset?)null;
                run.State = new(browser.Browser, browser.Status, decision.Reason, next, browser.Tabs);
                if (decision.Action is null) continue;

                token.ThrowIfCancellationRequested();
                if (!Equals(settings, session.Oshi) || account != session.UserId) break;
                var tab = decision.Tab;
                var revision = Guid.NewGuid().ToString("N");
                var mutation = new OshiBrowserMutation(decision.Action, revision, tab?.TabId, tab?.Revision, tab?.Login,
                    decision.Channel?.Channel);
                if (decision.Channel?.Login is { } login) reserved.Add(login);
                run.Pending = new(tab?.Revision, revision, false, decision.FallbackSince.HasValue);
                var acknowledged = await discovery.ApplyOshiMutationAsync(browser.Browser, decision.Channel?.Login, mutation, token);
                if (acknowledged && decision.Action == "close")
                    run.Pending = run.Pending with { Closed = true };
                if (!acknowledged) run.State = run.State with { Reason = "Waiting for browser confirmation" };
            }
            Changed?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally { _busy = false; }
    }

    private async Task<OshiLiveChannel[]> ReadSourceAsync(IOshiLiveSource source, OshiChannelKey[] keys,
        CancellationToken token)
    {
        try { return await source.ReadAsync(keys, token); }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or System.IO.IOException
            or System.Text.Json.JsonException || exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            return _live.Where(channel => channel.Channel.Provider == source.Provider)
                .Select(channel => channel with { Online = null }).ToArray();
        }
    }

    public void Dispose() => SetRunning(false);

    private sealed record PendingMutation(string? PreviousRevision, string Revision, bool Closed, bool IsFallback);

    private sealed class BrowserRun(string browser, Dictionary<string, DateTimeOffset>? deferred = null)
    {
        private OshiBrowserTab[] _previous = [];
        public Dictionary<int, OshiTurn> Turns { get; } = [];
        public Dictionary<(OshiChannelKey, string?), DateTimeOffset> Served { get; } = [];
        public Dictionary<string, DateTimeOffset> Deferred { get; } = deferred ?? new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? FallbackSince { get; set; }
        public PendingMutation? Pending { get; set; }
        public OshiBrowserState State { get; set; } = new(browser, "checking", "", null, []);

        public void Observe(OshiBrowserSnapshot snapshot, OshiLiveChannel[] live, DateTimeOffset now)
        {
            var byTab = snapshot.Tabs.ToDictionary(tab => tab.TabId);
            foreach (var tab in _previous)
            {
                if (byTab.TryGetValue(tab.TabId, out var current) && current.Revision == tab.Revision) continue;
                Turns.Remove(tab.TabId);
                var expected = Pending is { } pending && tab.Revision == pending.PreviousRevision &&
                    (pending.Closed || current?.Revision == pending.Revision);
                if (!expected) Deferred[tab.Login] = now + ManualCloseDelay;
            }

            var liveKeys = live.Where(channel => channel.Online != false)
                .Select(channel => (channel.Channel, channel.StreamId)).ToHashSet();
            foreach (var key in Served.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
                Served.Remove(key);
            foreach (var key in Deferred.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
                Deferred.Remove(key);
            foreach (var tab in snapshot.Tabs)
            {
                var channel = live.FirstOrDefault(channel => tab.Channel is { } key ? channel.Channel == key
                    : string.Equals(channel.Login, tab.Login, StringComparison.OrdinalIgnoreCase));
                var previous = Turns.GetValueOrDefault(tab.TabId);
                var stream = channel?.StreamId ?? previous?.StreamId;
                if (previous is null || previous.Revision != tab.Revision || stream != previous.StreamId)
                    Turns[tab.TabId] = new(tab.Revision, stream, now);
                if (channel?.Online == true) Served[(channel.Channel, stream)] = Turns[tab.TabId].Since;
            }
            if (Pending is { IsFallback: true } fallback && snapshot.Tabs.Any(tab => tab.Revision == fallback.Revision))
                FallbackSince = null;
            _previous = snapshot.Tabs;
            Pending = null;
        }
    }
}

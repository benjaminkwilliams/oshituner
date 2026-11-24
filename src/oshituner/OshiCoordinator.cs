using System.Windows.Threading;

namespace oshituner;

internal sealed record OshiBrowserState(string Browser, string Status, string Reason, DateTimeOffset? Next,
    OshiBrowserTab[] Tabs, int ReservedSlots = 0);
internal sealed record OshiState(OshiSettings Settings, bool Running, OshiBrowserState[] Browsers);

internal sealed class OshiCoordinator(TunerSession session, BrowserDiscovery discovery, IOshiLiveSource[] sources, LaunchLog log) : IDisposable
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DegradedStatusInterval = TimeSpan.FromMinutes(1);
    private readonly DateTimeOffset _clockOrigin = TimeProvider.System.GetUtcNow();
    private readonly long _timestampOrigin = TimeProvider.System.GetTimestamp();
    private DateTimeOffset Now => _clockOrigin + TimeProvider.System.GetElapsedTime(_timestampOrigin);
    private static readonly TimeSpan ManualCloseDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BrowserLaunchRetryDelay = TimeSpan.FromMinutes(1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, BrowserRun> _runs = new(StringComparer.Ordinal);
    private OshiLiveChannel[] _live = [];
    private DateTimeOffset _statusDue;
    private OshiSettings? _statusSettings;
    private string? _statusAccount;
    private CancellationTokenSource? _run;
    private bool _busy;
    private bool _observing;
    private readonly Queue<(string Browser, string Id, string Phase)> _recentRecoveries = new();
    private readonly HashSet<(string Browser, string Id, string Phase)> _reportedRecoveries = [];
    private readonly Dictionary<(string Browser, string Id), (string Login, DateTimeOffset Deadline)> _pendingRecoveries = [];

    public event Action? Changed;
    public OshiState State => new(session.Oshi, _run is not null, _runs.Values.Select(run => run.State).ToArray());

    public void SetRunning(bool running)
    {
        if (running == (_run is not null)) return;
        log.Write("Oshi", running ? "Oshi Watch started." : "Oshi Watch paused.");
        if (running)
        {
            foreach (var browser in _runs.Keys.ToArray())
            {
                RearmBrowser(browser);
                var previous = _runs[browser];
                _runs[browser] = new(browser, previous.Deferred)
                {
                    State = previous.State,
                    LastObserved = previous.LastObserved
                };
            }
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

    private BrowserRun RunFor(string browser)
    {
        if (!_runs.TryGetValue(browser, out var run)) _runs.Add(browser, run = new(browser));
        return run;
    }

    public void RearmBrowser(string browser)
    {
        var run = RunFor(browser);
        if (!run.LaunchSuspended) return;
        run.LaunchSuspended = false;
        run.NextLaunchAttempt = DateTimeOffset.MinValue;
        log.Write("Browser", $"{browser}: automatic launches resumed.");
    }

    public async Task ObserveWhilePausedAsync(CancellationToken token)
    {
        if (_run is not null || _observing) return;
        _observing = true;
        try
        {
            var browsers = await discovery.GetOshiTabsAsync(token);
            if (_run is not null || token.IsCancellationRequested) return;
            foreach (var browser in browsers)
            {
                var run = RunFor(browser.Browser);
                ObserveBrowserLifecycle(browser, run);
                if (browser.Status == "ready") run.Observe(browser, _live, Now);
                run.State = new(browser.Browser, browser.Status, "Paused", null, browser.Tabs, browser.ReservedSlots);
            }
            Changed?.Invoke();
        }
        finally { _observing = false; }
    }

    private void ObserveBrowserLifecycle(OshiBrowserSnapshot browser, BrowserRun run)
    {
        foreach (var recovery in browser.RecoveryEvents ?? [])
        {
            var key = (browser.Browser, recovery.Id, recovery.Phase);
            if (!_reportedRecoveries.Add(key)) continue;
            _recentRecoveries.Enqueue(key);
            if (_recentRecoveries.Count > 256) _reportedRecoveries.Remove(_recentRecoveries.Dequeue());
            var attempt = (browser.Browser, recovery.Id);
            if (recovery.Phase == "attempted") _pendingRecoveries[attempt] = (recovery.Login, Now.AddSeconds(20));
            else if (!_pendingRecoveries.Remove(attempt)) continue;
            var result = recovery.Phase switch
            {
                "attempted" => "reload attempted",
                "recovered" => "playback recovered",
                _ => "playback recovery unconfirmed"
            };
            var phase = recovery.Phase == "attempted" ? "attempt" : "result";
            log.Write("Twitch player", $"{browser.Browser}: {recovery.Login}: {result} after Error #1000.",
                $"twitch-player:{browser.Browser}:{recovery.Id}:{phase}");
        }
        foreach (var pending in _pendingRecoveries.Where(entry => entry.Key.Browser == browser.Browser && entry.Value.Deadline <= Now).ToArray())
        {
            _pendingRecoveries.Remove(pending.Key);
            log.Write("Twitch player", $"{browser.Browser}: {pending.Value.Login}: playback recovery unconfirmed after Error #1000.",
                $"twitch-player:{browser.Browser}:{pending.Key.Id}:result");
        }
        if (browser.Status == "ready")
        {
            if (browser.WindowCount > 0 || browser.Logins.Length > 0) RearmBrowser(browser.Browser);
            if (browser.WindowCount == 0 && run.WindowCount > 0) SuspendBrowser(browser.Browser, run, "last window closed");
            run.WindowCount = browser.WindowCount;
            run.HadBrowserContact = true;
            run.ContactMisses = 0;
            LogBrowserChanges(browser, run);
        }
        else if (run.HadBrowserContact && run.Startup is null && ++run.ContactMisses == 2)
            SuspendBrowser(browser.Browser, run, "browser contact lost");
    }

    private void LogBrowserChanges(OshiBrowserSnapshot browser, BrowserRun run)
    {
        var previous = run.LastObserved;
        run.LastObserved = browser with { Tabs = browser.Tabs.Concat(previous?.Tabs.Where(tab =>
            browser.ReservedTabIds?.Contains(tab.TabId) == true) ?? []).ToArray() };
        if (previous is null)
        {
            log.Write("Browser", $"{browser.Browser}: extension connected. {browser.Logins.Length} Twitch channels open, {browser.Tabs.Length} managed tabs.");
            return;
        }

        static string Sample(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase).Take(8));
        var opened = Sample(browser.Logins.Except(previous.Logins, StringComparer.OrdinalIgnoreCase));
        var left = Sample(previous.Logins.Except(browser.Logins, StringComparer.OrdinalIgnoreCase));
        if (opened.Length > 0 || left.Length > 0)
            log.Write("Browser", $"{browser.Browser}: Twitch tabs changed." +
                (left.Length == 0 ? "" : $" Left {left}.") + (opened.Length == 0 ? "" : $" Opened {opened}."));
        var previousTabs = previous.Tabs.ToDictionary(tab => tab.TabId);
        var currentTabs = run.LastObserved.Tabs.ToDictionary(tab => tab.TabId);
        foreach (var tab in browser.Tabs)
            if (previousTabs.TryGetValue(tab.TabId, out var old) && tab.Login != old.Login)
                log.Write("Browser", $"{browser.Browser}: managed tab changed from {old.Login} to {tab.Login} and kept its slot.");
        var assigned = Sample(browser.Tabs.Where(tab => !previousTabs.ContainsKey(tab.TabId)).Select(tab => tab.Login));
        var released = Sample(previous.Tabs.Where(tab => !currentTabs.ContainsKey(tab.TabId)).Select(tab => tab.Login));
        if (assigned.Length > 0 || released.Length > 0)
            log.Write("Browser", $"{browser.Browser}: managed tabs changed." +
                (released.Length == 0 ? "" : $" Released {released}.") + (assigned.Length == 0 ? "" : $" Assigned {assigned}."));
    }

    private void SuspendBrowser(string browser, BrowserRun run, string reason)
    {
        if (run.LaunchSuspended) return;
        run.LaunchSuspended = true;
        log.Write("Browser", $"{browser}: automatic launches suspended ({reason}).");
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
            var account = session.UserId;
            if (now >= _statusDue || !Equals(settings, _statusSettings) || account != _statusAccount)
            {
                var keys = settings.Ranks.SelectMany(group => group.Channels).Select(entry => entry.Channel).ToArray();
                var snapshots = await Task.WhenAll(sources.Select(source => ReadSourceAsync(source, keys, token)));
                var previous = _live.ToDictionary(channel => channel.Channel);
                var observedAt = Now;
                _live = snapshots.SelectMany(items => items).Select(channel =>
                {
                    var seen = previous.GetValueOrDefault(channel.Channel);
                    if (channel.Online is null && seen is not null)
                        return channel with { Login = channel.Login ?? seen.Login, StreamId = seen.StreamId,
                            FirstObservedLive = seen.FirstObservedLive };
                    if (channel.Online != true) return channel;
                    var first = seen is { Online: not false } && seen.StreamId == channel.StreamId
                        ? seen.FirstObservedLive : null;
                    return channel with { FirstObservedLive = first ?? observedAt };
                }).ToArray();
                _statusSettings = settings;
                _statusAccount = account;
                var degraded = _live.Length != 0 && _live.All(channel => channel.Online is null);
                _statusDue = Now + (degraded ? DegradedStatusInterval : StatusInterval);
            }
            token.ThrowIfCancellationRequested();
            if (!Equals(settings, session.Oshi)) return;
            if (session.UserId is null) _live = _live.Where(channel => channel.Channel.Provider != "twitch").ToArray();
            if (account != session.UserId) return;
            var browsers = await discovery.GetOshiTabsAsync(token);
            token.ThrowIfCancellationRequested();
            if (account != session.UserId) return;
            now = Now;
            if (!Equals(settings, session.Oshi)) return;
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var browser in browsers.OrderBy(browser => browser.Browser != settings.DefaultBrowser))
            {
                token.ThrowIfCancellationRequested();
                if (!Equals(settings, session.Oshi) || account != session.UserId) break;
                var run = RunFor(browser.Browser);
                ObserveBrowserLifecycle(browser, run);
                if (browser.Status == "ready") run.Observe(browser, _live, now);
                if (run.Startup is { } startup)
                {
                    if (now >= startup.Deadline || !Equals(settings, startup.Settings) || account != startup.Account ||
                        !_live.Any(channel => channel.Channel == startup.Channel && channel.Online == true &&
                            string.Equals(channel.Login, startup.Login, StringComparison.OrdinalIgnoreCase)))
                    {
                        run.Startup = null;
                        if (browser.Status != "ready") SuspendBrowser(browser.Browser, run, "startup was not confirmed");
                    }
                    else
                    {
                        reserved.Add(startup.Login);
                        token.ThrowIfCancellationRequested();
                        if (browser.Status == "ready" && await discovery.ApplyOshiMutationAsync(browser.Browser, startup.Login,
                            new("adopt_startup", startup.Assignment, Channel: startup.Channel, TabCap: settings.CapFor(browser.Browser)), token))
                        {
                            run.Startup = null;
                            if (startup.IsFallback) run.FallbackSince = null;
                            log.Write("Oshi", $"{browser.Browser}: managed startup stream {startup.Login}.");
                        }
                        run.State = new(browser.Browser, browser.Status, "Checking startup stream", null, browser.Tabs, browser.ReservedSlots);
                        continue;
                    }
                }
                if (run.LaunchSuspended)
                {
                    run.RankSkips.Clear();
                    run.State = new(browser.Browser, browser.Status, "Automatic launches suspended", null, browser.Tabs, browser.ReservedSlots);
                    continue;
                }
                var watchingElsewhere = browsers.Where(other => other.Browser != browser.Browser)
                    .SelectMany(other => other.Logins).Concat(reserved).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var mayStartBrowser = browser.Status == "browser unavailable" && !run.HadBrowserContact;
                var planningBrowser = mayStartBrowser ? browser with { Status = "ready" } : browser;
                var decision = OshiPlanner.Plan(settings, planningBrowser, _live, run.Turns, run.Served, run.Deferred,
                    run.FallbackSince, now, watchingElsewhere);
                run.FallbackSince = decision.FallbackSince;
                if (browser.Status != "ready") run.RankSkips.Clear();
                else if (decision.Action is null)
                {
                    var skips = (decision.RankSkips ?? []).ToHashSet();
                    foreach (var skip in skips.Except(run.RankSkips))
                        log.Write("Oshi", $"{browser.Browser}: skipped {skip.Channel} ({skip.Rank}); keeping {skip.CompetingChannel} ({skip.CompetingRank}) because {skip.Reason}.");
                    run.RankSkips = skips;
                }
                var next = decision.Next is { } due ? DateTimeOffset.UtcNow + (due - now) : (DateTimeOffset?)null;
                var statusSummary = $"{browser.Browser}: {browser.Status}; {browser.Tabs.Length + browser.ReservedSlots}/{settings.CapFor(browser.Browser)} managed, {browser.Logins.Length} Twitch channels.";
                if (run.LastStatusSummary != statusSummary)
                {
                    log.Write("Browser", statusSummary);
                    run.LastStatusSummary = statusSummary;
                }
                run.State = new(browser.Browser, browser.Status, decision.Reason, next, browser.Tabs, browser.ReservedSlots);
                if (mayStartBrowser)
                {
                    var reason = browser.Status;
                    if (decision.Action == "open" && decision.Channel is { Login: { } startupLogin } selected && now >= run.NextLaunchAttempt)
                    {
                        token.ThrowIfCancellationRequested();
                        run.NextLaunchAttempt = now + BrowserLaunchRetryDelay;
                        var assignment = Guid.NewGuid().ToString("N");
                        if (discovery.TryLaunchForOshi(browser.Browser, startupLogin, assignment))
                        {
                            run.Startup = new(startupLogin, selected.Channel, assignment, run.NextLaunchAttempt,
                                settings, account, decision.FallbackSince.HasValue);
                            reserved.Add(startupLogin);
                            reason = "Starting browser";
                            log.Write("Browser", $"{browser.Browser}: starting for {startupLogin}.");
                        }
                    }
                    run.State = run.State with { Reason = reason };
                    continue;
                }
                if (decision.Action is null) continue;

                token.ThrowIfCancellationRequested();
                if (!Equals(settings, session.Oshi) || account != session.UserId) break;
                var tab = decision.Tab;
                var revision = Guid.NewGuid().ToString("N");
                var mutation = new OshiBrowserMutation(decision.Action, revision, tab?.TabId, tab?.Revision, tab?.Login,
                    decision.Channel?.Channel, settings.CapFor(browser.Browser));
                if (decision.Channel?.Login is { } login) reserved.Add(login);
                run.Pending = new(tab?.Revision, revision, false, decision.FallbackSince.HasValue);
                var acknowledged = await discovery.ApplyOshiMutationAsync(browser.Browser, decision.Channel?.Login, mutation, token);
                var stream = tab?.Login is { } previousLogin && decision.Channel?.Login is { } newLogin
                    ? $"{previousLogin} to {newLogin}" : tab?.Login ?? decision.Channel?.Login;
                var actionSummary = $"{browser.Browser}: {decision.Action} {stream} ({decision.Reason})";
                if (acknowledged || run.LastFailedAction != actionSummary)
                    log.Write("Oshi", $"{actionSummary}: {(acknowledged ? "confirmed" : "not confirmed")}.");
                run.LastFailedAction = acknowledged ? null : actionSummary;
                if (acknowledged) run.RankSkips.Clear();
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

    private sealed record StartupStream(string Login, OshiChannelKey Channel, string Assignment, DateTimeOffset Deadline,
        OshiSettings Settings, string? Account, bool IsFallback);

    private sealed record PendingMutation(string? PreviousRevision, string Revision, bool Closed, bool IsFallback);

    private sealed class BrowserRun(string browser, Dictionary<string, DateTimeOffset>? deferred = null)
    {
        public HashSet<OshiRankSkip> RankSkips { get; set; } = [];
        private OshiBrowserTab[] _previous = [];
        public Dictionary<int, OshiTurn> Turns { get; } = [];
        public Dictionary<(OshiChannelKey, string?), DateTimeOffset> Served { get; } = [];
        public Dictionary<string, DateTimeOffset> Deferred { get; } = deferred ?? new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? FallbackSince { get; set; }
        public PendingMutation? Pending { get; set; }
        public DateTimeOffset NextLaunchAttempt { get; set; }
        public StartupStream? Startup { get; set; }
        public bool LaunchSuspended { get; set; }
        public bool HadBrowserContact { get; set; }
        public int ContactMisses { get; set; }
        public OshiBrowserSnapshot? LastObserved { get; set; }
        public int? WindowCount { get; set; }
        public string? LastStatusSummary { get; set; }
        public string? LastFailedAction { get; set; }
        public OshiBrowserState State { get; set; } = new(browser, "checking", "", null, []);

        public void Observe(OshiBrowserSnapshot snapshot, OshiLiveChannel[] live, DateTimeOffset now)
        {
            var byTab = snapshot.Tabs.ToDictionary(tab => tab.TabId);
            var reserved = snapshot.ReservedTabIds ?? [];
            foreach (var tab in _previous)
            {
                if (reserved.Contains(tab.TabId)) continue;
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
            _previous = snapshot.Tabs.Concat(_previous.Where(tab => reserved.Contains(tab.TabId))).ToArray();
            if (reserved.Length == 0) Pending = null;
        }
    }
}

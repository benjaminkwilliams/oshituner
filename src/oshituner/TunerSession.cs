using System.Net.Http;

namespace oshituner;

internal sealed partial class TunerSession : IDisposable
{
    private readonly SettingsStore _store;
    private TunerSettings _settings;
    private FollowedChannelRow[] _rows = [];

    public TunerSession(InstallProfile profile, HttpClient http)
    {
        _http = http;
        _store = new(profile);
        _settings = _store.Read();
        _tokens = new(profile);
        _browserDiscovery = new(new BrowserRegistration(profile));
        ClientId = Environment.GetEnvironmentVariable("TWITCH_CLIENT_ID")?.Trim() ?? _settings.TwitchClientId ?? "";
        Sort = _settings.Sort is { } saved && saved.IsValid() ? saved : GridSort.Default();
    }

    public static IReadOnlyList<int> RefreshIntervals { get; } = [5, 10, 15, 30, 60];

    public int AutoRefreshMinutes => _settings.AutoRefreshMinutes is { } minutes && RefreshIntervals.Contains(minutes) ? minutes : 30;

    public void SetAutoRefreshMinutes(int minutes)
    {
        if (!RefreshIntervals.Contains(minutes))
            throw new ArgumentException("Choose 5, 10, 15, 30, or 60 minutes.");
        if (minutes == AutoRefreshMinutes) return;
        SaveSettings(_settings with { AutoRefreshMinutes = minutes });
        Changed?.Invoke();
    }

    public ThemeSettings Theme => _settings.Theme is { } theme && theme.IsValid() ? theme : new();

    public void SetTheme(ThemeSettings theme) => SaveSettings(_settings with { Theme = theme });

    public WorkspaceState? Workspace => _settings.Workspace;

    public void SetWorkspace(WorkspaceState state) => SaveSettings(_settings with { Workspace = state });

    private static readonly OshiSettings DefaultOshi = OshiSettings.Create();
    public OshiSettings Oshi => _settings.Oshi ?? DefaultOshi;

    public OshiCoordinator CreateOshiCoordinator(LaunchLog log) => new(this, _browserDiscovery, [TwitchOshiSource], log);

    public bool CanRank(OshiChannelKey channel) => _rows.Any(row =>
        row.Provider == channel.Provider && row.ChannelId == channel.Id &&
        (row.Followed || (row.Subbed == true && !row.SubscriptionCheckStale)));

    public void SetOshi(OshiSettings settings)
    {
        if (!settings.IsValid())
            throw new ArgumentException($"Check unique ranks and channels, browser tab limits (0 to {OshiSettings.MaxBrowserTabs}), rank names (up to {OshiRank.MaxNameLength} characters), row timings (5 to 60 minutes), and Auto Launch cooldown (1 to 5 hours).");

        var existing = Oshi.Ranks.SelectMany(rank => rank.Channels).Select(entry => entry.Channel).ToHashSet();
        foreach (var entry in settings.Ranks.SelectMany(rank => rank.Channels))
        {
            if (!existing.Contains(entry.Channel) && !CanRank(entry.Channel))
                throw new ArgumentException($"Follow or subscribe to {entry.Label} before ranking that channel.");
        }

        SaveSettings(_settings with { Oshi = settings });
        Changed?.Invoke();
    }

    public event Action? Changed;
    public GridSort Sort { get; private set; }
    public IReadOnlyList<FollowedChannelRow> Channels => _channelProjection ?? _rows;

    public void SetSort(GridSort sort)
    {
        if (!sort.IsValid()) throw new ArgumentException("Include each sort group exactly once and select a valid secondary sort.");
        SaveSettings(_settings with { Sort = sort });
        Sort = sort;
        _rows = Sort.Apply(_rows);
        RebuildChannelProjection();
        Changed?.Invoke();
    }

    private void SaveSettings(TunerSettings settings)
    {
        _store.Save(settings);
        _settings = settings;
    }
}

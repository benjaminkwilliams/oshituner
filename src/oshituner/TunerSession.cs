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

    public int? PreferredPort => _settings.ControlPort;

    public void RememberPort(int port)
    {
        if (PreferredPort != port)
            SaveSettings(_settings with { ControlPort = port });
    }

    private void SaveSettings(TunerSettings settings)
    {
        _store.Save(settings);
        _settings = settings;
    }
}

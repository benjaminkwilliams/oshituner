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
        ClientId = Environment.GetEnvironmentVariable("TWITCH_CLIENT_ID")?.Trim() ?? _settings.TwitchClientId ?? "";
        Sort = _settings.Sort is { } saved && saved.IsValid() ? saved : GridSort.Default();
    }

    public event Action? Changed;
    public GridSort Sort { get; private set; }
    public IReadOnlyList<FollowedChannelRow> Channels => _rows;

    public void SetSort(GridSort sort)
    {
        if (!sort.IsValid()) throw new ArgumentException("Include each sort group exactly once and select a valid secondary sort.");
        SaveSettings(_settings with { Sort = sort });
        Sort = sort;
        _rows = Sort.Apply(_rows);
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

using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace oshituner;

internal sealed partial class TunerSession
{
    private readonly HashSet<string> _watchLogins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolvedWatchLogins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FollowedChannelRow> _watchOnlyRows = new(StringComparer.OrdinalIgnoreCase);
    private TwitchAccess? _watchAccess;
    private FollowedChannelRow[]? _channelProjection;
    private bool _watchRecheck;
    private bool _watchUpdateRunning;
    private bool _watchUpdatePending;
    private bool _edgeAvailable;
    private bool _chromeAvailable;

    public Task ObserveWatchChangesAsync(Action changed, CancellationToken token) =>
        _browserDiscovery.ObserveWatchChangesAsync(changed, token);

    public async Task WatchChannelAsync(string channelId, string browser, CancellationToken token)
    {
        var row = Channels.FirstOrDefault(channel => channel.ChannelId == channelId)
            ?? throw new ArgumentException("The channel is not in the current grid.");
        if (!row.IsLive) throw new InvalidOperationException("Only channels that are live can be opened from WATCH.");
        await _browserDiscovery.OpenWatchChannelAsync(browser, row.Login, token);
        await SyncWatchTabsAsync(token);
    }

    public async Task SyncWatchTabsAsync(CancellationToken token)
    {
        if (_disposed || _watchAccess is null) return;
        if (_watchUpdateRunning)
        {
            _watchUpdatePending = true;
            return;
        }
        _watchUpdateRunning = true;
        try
        {
            do
            {
                _watchUpdatePending = false;
                if (_watchAccess is not { } access || token.IsCancellationRequested) break;
                var snapshots = await _browserDiscovery.GetWatchTabsAsync(token);
                if (_disposed || _watchAccess?.UserId != access.UserId) break;
                UpdateWatchSnapshot(snapshots, out var newLogins, out var activeLogins);
                await EnrichWatchRowsAsync(access, newLogins, activeLogins, token);
            }
            while (_watchUpdatePending);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _watchUpdateRunning = false; }
    }

    private void UpdateWatchSnapshot(BrowserWatchSnapshot[] snapshots, out string[] newLogins, out string[] activeLogins)
    {
        var logins = snapshots.SelectMany(snapshot => snapshot.Logins).ToHashSet(StringComparer.OrdinalIgnoreCase);
        activeLogins = snapshots.SelectMany(snapshot => snapshot.ActiveLogins).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        newLogins = (_watchRecheck ? logins : logins.Except(_watchLogins, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (_watchRecheck) _unresolvedWatchLogins.Clear();
        _watchRecheck = false;
        var edge = snapshots.Any(snapshot => snapshot is { Browser: "edge", Available: true });
        var chrome = snapshots.Any(snapshot => snapshot is { Browser: "chrome", Available: true });
        if (_watchLogins.SetEquals(logins) && _edgeAvailable == edge && _chromeAvailable == chrome) return;

        _watchLogins.Clear();
        _watchLogins.UnionWith(logins);
        _unresolvedWatchLogins.IntersectWith(logins);
        foreach (var login in _watchOnlyRows.Keys.Where(login => !logins.Contains(login)).ToArray())
            _watchOnlyRows.Remove(login);
        _edgeAvailable = edge;
        _chromeAvailable = chrome;
        RebuildChannelProjection();
        Changed?.Invoke();
    }

    private async Task EnrichWatchRowsAsync(TwitchAccess access, string[] newLogins, string[] activeLogins, CancellationToken token)
    {
        var rowsByLogin = _rows.Concat(_watchOnlyRows.Values).DistinctBy(row => row.Login, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(row => row.Login, StringComparer.OrdinalIgnoreCase);
        // A selected tab can add a channel. Background tabs still count for channels already in the grid.
        var missing = activeLogins.Where(login => !rowsByLogin.ContainsKey(login) && !_unresolvedWatchLogins.Contains(login)).ToArray();
        var existing = newLogins.Where(rowsByLogin.ContainsKey).Select(login => rowsByLogin[login]).ToArray();
        if (missing.Length == 0 && existing.Length == 0) return;

        var api = new TwitchApiClient(_http, ClientId);
        try
        {
            var users = await api.GetUsersAsync(access, missing, byLogin: true, token);
            var ids = existing.Select(row => row.ChannelId).Concat(users.Select(user => user.Id)).Distinct().ToArray();
            var live = await api.GetLiveStatusAsync(access, ids, token);
            if (_disposed || _watchAccess != access || token.IsCancellationRequested) return;

            _unresolvedWatchLogins.UnionWith(missing.Except(users.Select(user => user.Login), StringComparer.OrdinalIgnoreCase));
            var streams = live.Streams.ToLookup(item => item.UserId, StringComparer.Ordinal);
            FollowedChannelRow Update(FollowedChannelRow row)
            {
                var stream = streams[row.ChannelId].FirstOrDefault();
                if (stream is null && !live.Covered.Contains(row.ChannelId)) return row;
                return row with
                {
                    Online = stream is not null,
                    GameName = stream?.GameName ?? "",
                    StreamTitle = stream?.Title ?? "",
                    ViewerCount = stream?.ViewerCount ?? 0
                };
            }

            var changed = false;
            foreach (var user in users)
            {
                if (!_watchLogins.Contains(user.Login) || _rows.Any(row => row.ChannelId == user.Id)) continue;
                _watchOnlyRows[user.Login] = Update(new("twitch", user.Id, user.DisplayName, user.Login, null,
                    false, null, null, "", "", 0));
                changed = true;
            }

            foreach (var row in existing)
            {
                if (!_watchLogins.Contains(row.Login)) continue;
                var index = Array.FindIndex(_rows, candidate => candidate.ChannelId == row.ChannelId);
                if (index >= 0)
                {
                    var updated = Update(_rows[index]);
                    changed |= updated != _rows[index];
                    _rows[index] = updated;
                }
                else if (_watchOnlyRows.TryGetValue(row.Login, out var temporary))
                {
                    var updated = Update(temporary);
                    changed |= updated != temporary;
                    _watchOnlyRows[row.Login] = updated;
                }
            }

            if (changed)
            {
                RebuildChannelProjection();
                Changed?.Invoke();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            || exception is OperationCanceledException && !token.IsCancellationRequested) { }
    }

    private void RebuildChannelProjection()
    {
        _channelProjection = Sort.Apply(_rows.Concat(_watchOnlyRows.Values)
            .DistinctBy(row => row.ChannelId).Select(row => row with
            {
                BrowserTabOpen = _watchLogins.Contains(row.Login),
                EdgeAvailable = _edgeAvailable,
                ChromeAvailable = _chromeAvailable
            }));
    }

    private void ClearWatchRows()
    {
        _watchAccess = null;
        _watchLogins.Clear();
        _watchOnlyRows.Clear();
        _unresolvedWatchLogins.Clear();
        _channelProjection = null;
        _watchRecheck = true;
        _edgeAvailable = false;
        _chromeAvailable = false;
    }
}

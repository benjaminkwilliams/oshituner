using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace oshituner;

public partial class OshiView : UserControl
{
    private ToolTip? _keyboardHelp;

    private void ExplainedHeader_GotKeyboardFocus(object sender, RoutedEventArgs args)
    {
        if (sender is not TextBlock header) return;
        _keyboardHelp = new ToolTip { PlacementTarget = header, Content = header.ToolTip, IsOpen = true };
    }

    private void ExplainedHeader_LostKeyboardFocus(object sender, RoutedEventArgs args)
    {
        if (_keyboardHelp is not null) _keyboardHelp.IsOpen = false;
        _keyboardHelp = null;
    }

    private OshiState? _state;
    private IReadOnlyList<FollowedChannelRow> _channels = [];
    private Func<OshiChannelKey, bool>? _canRank;
    private bool _showUnranked;

    public OshiView() => InitializeComponent();

    internal Action<OshiSettings>? SaveRequested { get; set; }
    internal Action<bool>? RunningRequested { get; set; }
    internal void FocusRanked() => Ranked.Focus();

    internal void Render(OshiState state, IReadOnlyList<FollowedChannelRow> channels, Func<OshiChannelKey, bool> canRank)
    {
        _state = state;
        _channels = channels;
        _canRank = canRank;
        AutomationStatus.Text = state.Running ? "Oshi Watch running" : "Oshi Watch paused";

        var browsers = BrowserRegistration.Targets.Select(target =>
        {
            var browser = state.Browsers.FirstOrDefault(item => item.Browser == target.Id);
            var next = browser?.Next is { } due ? Math.Max(0, Math.Ceiling((due - DateTimeOffset.UtcNow).TotalMinutes)).ToString() : "";
            var name = target.Id == "edge" ? "Edge" : "Chrome";
            return new BrowserRow(target.Id, state.Settings.DefaultBrowser == target.Id ? name + " (default)" : name,
                state.Settings.CapFor(target.Id), browser is { Status: "ready" } ? (browser.Tabs.Length + browser.ReservedSlots).ToString() : "—",
                state.Running ? browser?.Reason ?? "Checking" : "Paused", next);
        }).ToArray();
        if (Browsers.ItemsSource is not BrowserRow[] previous || !browsers.SequenceEqual(previous))
        {
            if (((IEditableCollectionView)Browsers.Items).IsEditingItem)
                return;
            Browsers.ItemsSource = browsers;
        }
        RenderRows();
    }

    private void RenderRows()
    {
        if (_state is not { } state || ((IEditableCollectionView)Ranked.Items).IsEditingItem) return;
        var settings = state.Settings;
        var entries = settings.Ranks.SelectMany((rank, index) => rank.Channels.Select(entry =>
            CreateRow(entry, rank, index)));
        if (_showUnranked)
        {
            var ranked = settings.Ranks.SelectMany(rank => rank.Channels).Select(entry => entry.Channel).ToHashSet();
            entries = entries.Concat(_channels.Where(channel =>
            {
                var key = new OshiChannelKey(channel.Provider, channel.ChannelId);
                return !ranked.Contains(key) && _canRank?.Invoke(key) == true;
            }).Select(channel => CreateRow(new(new(channel.Provider, channel.ChannelId), channel.DisplayName), null, -1)));
        }

        var rows = entries.ToArray();
        if (Ranked.ItemsSource is ChannelRow[] previous && rows.SequenceEqual(previous)) return;
        var selected = (Ranked.SelectedItem as ChannelRow)?.Entry.Channel;
        Ranked.ItemsSource = rows;
        Ranked.SelectedItem = rows.FirstOrDefault(row => row.Entry.Channel == selected) ?? rows.FirstOrDefault();

        ChannelRow CreateRow(OshiEntry entry, OshiRank? rank, int index)
        {
            var timing = settings.TimingFor(entry.Channel);
            var browser = settings.BrowserFor(entry) == "edge" ? "Edge" : "Chrome";
            return new(entry, rank?.Id, index >= 0 ? (index + 1).ToString() : "", rank?.Name ?? "", entry.Label,
                entry.Channel.Provider, rank is null ? "" : entry.Browser is null ? $"{browser} (default)" : browser,
                timing.RotationMinutes ?? OshiSettings.DefaultWatchMinutes,
                timing.FallbackMinutes ?? OshiSettings.DefaultFallbackMinutes, index >= 0 && index % 2 == 0,
                _canRank?.Invoke(entry.Channel) == true ? "" : "Follow or sub required to re-add");
        }
    }

    private void SelectRowForMenu(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
            Ranked.SelectedItem = (ItemsControl.ContainerFromElement(Ranked, source) as DataGridRow)?.Item;
    }

    private void RankMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_state is not { } state || sender is not ContextMenu menu) return;
        menu.Items.Clear();
        menu.Items.Add(Option(state.Running ? "Pause Oshi Watch" : "Start Oshi Watch",
            () => RunningRequested?.Invoke(!state.Running)));
        menu.Items.Add(Choice("Show unranked follows and subs", _showUnranked, () =>
        {
            _showUnranked = !_showUnranked;
            RenderRows();
        }));
        menu.Items.Add(BrowserMenu("Default browser", state.Settings.DefaultBrowser,
            browser => Save(settings => settings with { DefaultBrowser = browser! }), allowDefault: false));

        if (Ranked.SelectedItem is not ChannelRow row) return;
        menu.Items.Add(new Separator());
        var ranks = new MenuItem { Header = "Set Rank" };
        foreach (var (rank, index) in state.Settings.Ranks.Select((rank, index) => (rank, index)))
            ranks.Items.Add(Choice(rank.NumberedName(index), rank.Id == row.RankId, () => SetRank(row, rank.Id)));
        ranks.Items.Add(Option("New rank", () => SetRank(row, Guid.NewGuid().ToString("N"))));
        if (row.RankId is not null)
            ranks.Items.Add(Option("Unrank", () => SetRank(row, null)));
        menu.Items.Add(ranks);

        if (row.RankId is { } rankId)
        {
            var index = Array.FindIndex(state.Settings.Ranks, rank => rank.Id == rankId);
            var channels = state.Settings.Ranks[index].Channels;
            var position = Array.FindIndex(channels, item => item.Channel == row.Entry.Channel);
            var order = new MenuItem { Header = "Move within rank" };
            order.Items.Add(Option("Up", () => Move(row, -1, withinRank: true), position > 0));
            order.Items.Add(Option("Down", () => Move(row, 1, withinRank: true), position < channels.Length - 1));
            menu.Items.Add(order);
            var priority = new MenuItem { Header = "Move whole rank" };
            priority.Items.Add(Option("Up", () => Move(row, -1, withinRank: false), index > 0));
            priority.Items.Add(Option("Down", () => Move(row, 1, withinRank: false), index < state.Settings.Ranks.Length - 1));
            menu.Items.Add(priority);
            menu.Items.Add(BrowserMenu("Set browser", row.Entry.Browser, browser => Save(settings => settings with
            {
                Ranks = settings.Ranks.Select(rank => rank.Id != rankId ? rank : rank with
                {
                    Channels = rank.Channels.Select(entry => entry.Channel == row.Entry.Channel ? entry with { Browser = browser } : entry).ToArray()
                }).ToArray()
            }), allowDefault: true));
        }

        var reset = new MenuItem { Header = "Reset timing to default" };
        reset.Items.Add(Option($"Minimum watch ({OshiSettings.DefaultWatchMinutes} min)", () => SetTiming(row, true, null)));
        reset.Items.Add(Option($"Fallback ({OshiSettings.DefaultFallbackMinutes} min)", () => SetTiming(row, false, null)));
        menu.Items.Add(reset);
    }

    private static MenuItem BrowserMenu(string header, string? current, Action<string?> apply, bool allowDefault)
    {
        var menu = new MenuItem { Header = header };
        if (allowDefault) menu.Items.Add(Choice("Default", current is null, () => apply(null)));
        menu.Items.Add(Choice("Edge", current == "edge", () => apply("edge")));
        menu.Items.Add(Choice("Chrome", current == "chrome", () => apply("chrome")));
        return menu;
    }

    private static MenuItem Option(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Choice(string header, bool selected, Action action)
    {
        var item = Option(header, action);
        item.IsCheckable = true;
        item.IsChecked = selected;
        return item;
    }

    private void SetRank(ChannelRow row, string? destination)
    {
        if (row.RankId == destination) return;
        Save(settings =>
        {
            var ranks = settings.Ranks.Select(rank =>
            {
                if (rank.Id == row.RankId)
                    return rank with { Channels = rank.Channels.Where(entry => entry.Channel != row.Entry.Channel).ToArray() };
                if (rank.Id == destination)
                    return rank with { Channels = [.. rank.Channels, row.Entry] };
                return rank;
            }).Where(rank => rank.Channels.Length != 0).ToList();
            if (destination is not null && !ranks.Any(rank => rank.Id == destination))
                ranks.Add(new(destination, [row.Entry]));
            return settings with { Ranks = ranks.ToArray() };
        });
    }

    private void Move(ChannelRow row, int offset, bool withinRank)
    {
        Save(settings =>
        {
            var ranks = settings.Ranks.ToArray();
            var index = Array.FindIndex(ranks, rank => rank.Id == row.RankId);
            if (index < 0) return settings;
            if (withinRank)
            {
                var channels = ranks[index].Channels.ToArray();
                var position = Array.FindIndex(channels, entry => entry.Channel == row.Entry.Channel);
                if (position < 0 || !Swap(channels, position, position + offset)) return settings;
                ranks[index] = ranks[index] with { Channels = channels };
            }
            else if (!Swap(ranks, index, index + offset)) return settings;
            return settings with { Ranks = ranks };
        });
    }

    private static bool Swap<T>(T[] items, int first, int second)
    {
        if (second < 0 || second >= items.Length) return false;
        (items[first], items[second]) = (items[second], items[first]);
        return true;
    }

    private void BrowserCell_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not BrowserRow row || e.EditingElement is not TextBox text) return;
        if (!int.TryParse(text.Text, out var value) || value is < 0 or > OshiSettings.MaxBrowserTabs)
        {
            Reject(e, $"Enter a tab limit from 0 to {OshiSettings.MaxBrowserTabs}.");
            return;
        }
        if (value != row.Cap) AfterEdit(Browsers, () => Save(settings => row.Id == "edge"
            ? settings with { EdgeCap = value } : settings with { ChromeCap = value }));
    }

    private void RankCell_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not ChannelRow row || e.EditingElement is not TextBox text) return;
        var value = text.Text.Trim();
        if (e.Column == RankNameColumn)
        {
            if (row.RankId is null || value.Length > OshiRank.MaxNameLength)
            {
                Reject(e, row.RankId is null ? "Set a rank before naming it." : $"Use up to {OshiRank.MaxNameLength} characters.");
                return;
            }
            if (value != row.RankName) AfterEdit(Ranked, () => Save(settings => settings with
            {
                Ranks = settings.Ranks.Select(rank => rank.Id == row.RankId
                    ? rank with { Name = value.Length == 0 ? null : value } : rank).ToArray()
            }));
            return;
        }

        var rotation = e.Column == RotationColumn;
        if (!rotation && e.Column != FallbackColumn) return;
        int? minutes = int.TryParse(value, out var parsed) ? parsed : null;
        if (value.Length != 0 && (!minutes.HasValue || !OshiSettings.ValidTiming(minutes)))
        {
            Reject(e, $"Enter {OshiSettings.MinTimingMinutes} to {OshiSettings.MaxTimingMinutes} minutes, or leave blank for the default.");
            return;
        }
        if (minutes != (rotation ? row.Rotation : row.Fallback)) AfterEdit(Ranked, () => SetTiming(row, rotation, minutes));
    }

    private void SetTiming(ChannelRow row, bool rotation, int? minutes) => Save(settings =>
    {
        var current = settings.TimingFor(row.Entry.Channel);
        return settings.WithTiming(rotation ? current with { RotationMinutes = minutes } : current with { FallbackMinutes = minutes });
    });

    private void Reject(DataGridCellEditEndingEventArgs args, string message)
    {
        args.Cancel = true;
        GridFeedback.Text = message;
    }

    private void AfterEdit(DataGrid grid, Action action) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (grid.CommitEdit(DataGridEditingUnit.Row, true)) action();
    });

    private void Save(Func<OshiSettings, OshiSettings> change)
    {
        if (_state is null) return;
        GridFeedback.Text = "";
        SaveRequested?.Invoke(change(_state.Settings));
    }

    private sealed record BrowserRow(string Id, string Name, int Cap, string Managed, string Status, string Next)
    {
        public string EditCap { get; set; } = Cap.ToString();
    }

    private sealed record ChannelRow(OshiEntry Entry, string? RankId, string RankNumber, string RankName,
        string ChannelName, string Provider, string Browser, int Rotation, int Fallback, bool Shaded, string Eligibility)
    {
        public string EditRankName { get; set; } = RankName;
        public string EditRotation { get; set; } = Rotation.ToString();
        public string EditFallback { get; set; } = Fallback.ToString();
    }
}

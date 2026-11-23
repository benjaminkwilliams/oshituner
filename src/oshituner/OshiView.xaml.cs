using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace oshituner;

public partial class OshiView : UserControl
{
    private OshiState? _state;
    private Point? _dragOrigin;
    private RankedItem? _dragItem;

    public OshiView() => InitializeComponent();

    internal Action<OshiSettings>? SaveRequested { get; set; }
    internal Action<bool>? RunningRequested { get; set; }
    internal void FocusRanked() => Ranked.Focus();

    internal void Render(OshiState state, Func<OshiChannelKey, bool> canRank)
    {
        var settingsChanged = !ReferenceEquals(_state?.Settings, state.Settings);
        _state = state;
        RunningButton.Content = state.Running ? "Pause" : "Start";
        BrowserStatus.Text = !state.Running ? "Paused" : state.Browsers.Length == 0 ? "Checking browsers…"
            : string.Join(Environment.NewLine, state.Browsers.Select(browser =>
            {
                var label = browser.Browser == "edge" ? "Edge" : "Chrome";
                var tabs = browser.Tabs.Length == 0 ? "" : $" · {string.Join(", ", browser.Tabs.Select(tab => tab.Login))}";
                var next = browser.Next is { } due
                    ? $" · {Math.Max(0, Math.Ceiling((due - DateTimeOffset.UtcNow).TotalMinutes))} min remaining" : "";
                return $"{label} · {browser.Reason}{tabs}{next}";
            }));

        var selected = (Ranked.SelectedItem as RankedItem)?.Entry.Channel;
        var rows = state.Settings.Ranks.SelectMany((rank, index) => rank.Channels.Select(entry =>
        {
            var eligibility = canRank(entry.Channel) ? "" : " · Follow or sub required to re-add";
            return new RankedItem(rank.Id, entry, $"{index + 1}. {entry.Label} · {entry.Channel.Provider} · {state.Settings.BrowserFor(entry)}{eligibility}");
        })).ToArray();
        if (Ranked.ItemsSource is not RankedItem[] previous || !rows.SequenceEqual(previous))
        {
            Ranked.ItemsSource = rows;
            Ranked.SelectedItem = rows.FirstOrDefault(item => item.Entry.Channel == selected) ?? rows.FirstOrDefault();
        }

        if (!settingsChanged) return;
        DefaultBrowser.SelectedValue = state.Settings.DefaultBrowser;
        EdgeCap.Text = state.Settings.EdgeCap.ToString();
        ChromeCap.Text = state.Settings.ChromeCap.ToString();
    }

    private void Running_Click(object sender, RoutedEventArgs e) => RunningRequested?.Invoke(_state?.Running != true);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null) return;
        var edge = int.TryParse(EdgeCap.Text, out var edgeCap) ? edgeCap : -1;
        var chrome = int.TryParse(ChromeCap.Text, out var chromeCap) ? chromeCap : -1;
        SaveRequested?.Invoke(_state.Settings with
        {
            DefaultBrowser = (string)DefaultBrowser.SelectedValue, EdgeCap = edge, ChromeCap = chrome
        });
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) =>
        EntryBrowser.SelectedValue = (Ranked.SelectedItem as RankedItem)?.Entry.Browser ?? "";

    private void MoveRank_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || Ranked.SelectedItem is not RankedItem item || sender is not Button { Tag: string offset }) return;
        var ranks = _state.Settings.Ranks.ToArray();
        var index = Array.FindIndex(ranks, rank => rank.Id == item.RankId);
        var target = index + int.Parse(offset);
        if (target < 0 || target >= ranks.Length) return;
        (ranks[index], ranks[target]) = (ranks[target], ranks[index]);
        SaveRequested?.Invoke(_state.Settings with { Ranks = ranks });
    }

    private void Tie_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || Ranked.SelectedItem is not RankedItem item) return;
        var ranks = _state.Settings.Ranks;
        var index = Array.FindIndex(ranks, rank => rank.Id == item.RankId);
        if (index > 0) MoveEntry(item, ranks[index - 1].Id, false);
    }

    private void Separate_Click(object sender, RoutedEventArgs e)
    {
        if (_state is not null && Ranked.SelectedItem is RankedItem item &&
            _state.Settings.Ranks.First(rank => rank.Id == item.RankId).Channels.Length > 1)
            MoveEntry(item, null, true);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Ranked.SelectedItem is RankedItem item) MoveEntry(item, null, false);
    }

    private void SetBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || Ranked.SelectedItem is not RankedItem item) return;
        var browser = EntryBrowser.SelectedValue as string;
        var ranks = _state.Settings.Ranks.Select(rank => rank.Id != item.RankId ? rank : rank with
        {
            Channels = rank.Channels.Select(entry => entry.Channel != item.Entry.Channel ? entry
                : entry with { Browser = string.IsNullOrEmpty(browser) ? null : browser }).ToArray()
        }).ToArray();
        SaveRequested?.Invoke(_state.Settings with { Ranks = ranks });
    }

    private void MoveEntry(RankedItem item, string? target, bool separate)
    {
        if (_state is null || item.RankId == target) return;
        var ranks = new List<OshiRank>();
        foreach (var rank in _state.Settings.Ranks)
        {
            if (rank.Id == item.RankId)
            {
                var retained = rank.Channels.Where(entry => entry.Channel != item.Entry.Channel).ToArray();
                if (retained.Length > 0) ranks.Add(rank with { Channels = retained });
                if (separate) ranks.Add(new(Guid.NewGuid().ToString("N"), [item.Entry]));
            }
            else ranks.Add(rank.Id == target ? rank with { Channels = [.. rank.Channels, item.Entry] } : rank);
        }
        SaveRequested?.Invoke(_state.Settings with { Ranks = [.. ranks] });
    }

    private RankedItem? ItemAt(object source) => source is DependencyObject element &&
        ItemsControl.ContainerFromElement(Ranked, element) is ListBoxItem { Content: RankedItem item } ? item : null;

    private void Drag_Start(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(Ranked);
        _dragItem = ItemAt(e.OriginalSource);
    }

    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragOrigin is not { } origin || _dragItem is not { } item) return;
        var delta = e.GetPosition(Ranked) - origin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragOrigin = null;
        DragDrop.DoDragDrop(Ranked, item, DragDropEffects.Move);
    }

    private void Rank_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RankedItem)) is RankedItem item && ItemAt(e.OriginalSource) is { } target)
            MoveEntry(item, target.RankId, false);
    }

    private sealed record RankedItem(string RankId, OshiEntry Entry, string Label);
}

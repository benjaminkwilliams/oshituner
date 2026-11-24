using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;

namespace oshituner;

public partial class TwitchStatusView : UserControl
{
    private readonly FollowedChannelViewModel _viewModel = new();
    private IReadOnlyList<FollowedChannelRow>? _renderedRows;
    private OshiRank[]? _renderedRanks;
    private int _refreshMinutes;

    internal Action? RefreshRequested { get; set; }
    internal Action<int>? IntervalChanged { get; set; }
    internal Action? EditSortRequested { get; set; }
    internal Func<FollowedChannelRow, string, bool, Task>? WatchRequested { get; set; }
    internal Action<string>? CopyLoginRequested { get; set; }
    internal Func<FollowedChannelRow, OshiRank[]?>? RankChoices { get; set; }
    internal Action<FollowedChannelRow, string?>? AddOshiRequested { get; set; }

    public TwitchStatusView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _columnView = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.FollowedChannels);
        _columnView.Filter = MatchesFilters;
        foreach (var minutes in TunerSession.RefreshIntervals)
        {
            var option = new MenuItem { Header = minutes.ToString(), Tag = minutes, IsCheckable = true };
            option.Click += RefreshInterval_Click;
            RefreshMenu.Items.Add(option);
        }
    }

    internal void Render(TunerSession session)
    {
        var connected = session.UserId is not null;
        ListToolbar.Visibility = connected && session.ActivationUri is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        RefreshNow.IsEnabled = !session.Busy;
        _refreshMinutes = session.AutoRefreshMinutes;
        var helpText = $"Automatic refresh every {_refreshMinutes} minutes";
        RefreshButton.ToolTip = helpText;
        System.Windows.Automation.AutomationProperties.SetHelpText(RefreshButton, helpText);
        MarkRefreshInterval();

        if (!ReferenceEquals(_renderedRows, session.Channels) || !ReferenceEquals(_renderedRanks, session.Oshi.Ranks))
        {
            var selected = (FollowedChannelsGrid.SelectedItem as FollowedChannelRow)?.ChannelId;
            _renderedRows = session.Channels;
            _renderedRanks = session.Oshi.Ranks;
            var labels = _renderedRanks.SelectMany((rank, index) => rank.Channels.Select(entry =>
                (entry.Channel, Name: rank.GridName(index), Order: index)))
                .ToDictionary(entry => entry.Channel);
            _viewModel.FollowedChannels.Clear();
            foreach (var row in _renderedRows)
                _viewModel.FollowedChannels.Add(labels.TryGetValue(new(row.Provider, row.ChannelId), out var label)
                    ? row with { RankName = label.Name, RankOrder = label.Order } : row);
            if (selected is not null)
                FollowedChannelsGrid.SelectedItem = _columnView.Cast<FollowedChannelRow>().FirstOrDefault(row => row.ChannelId == selected);
        }

        FollowCountText.Text = $"{session.Channels.Count(row => row.Followed)} followed";
        LiveCountText.Text = $"{session.Channels.Count(row => row.IsLive)} live";
    }

    internal void FocusGrid() => FollowedChannelsGrid.Focus();

    private void OpenRefreshMenu_Click(object sender, RoutedEventArgs e)
    {
        RefreshMenu.PlacementTarget = RefreshButton;
        RefreshMenu.IsOpen = true;
    }

    private void RefreshNow_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void RefreshInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: int minutes } && minutes != _refreshMinutes)
            IntervalChanged?.Invoke(minutes);
        MarkRefreshInterval();
    }

    private void MarkRefreshInterval()
    {
        foreach (var item in RefreshMenu.Items.OfType<MenuItem>())
            if (item.Tag is int minutes) item.IsChecked = minutes == _refreshMinutes;
    }

    private void SelectChannelForContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Visual visual && IsColumnHeader(visual)) return;
        if (e.OriginalSource is DependencyObject element)
            FollowedChannelsGrid.SelectedItem = (ItemsControl.ContainerFromElement(FollowedChannelsGrid, element) as DataGridRow)?.Item;
    }

    private static bool IsColumnHeader(Visual visual)
    {
        for (DependencyObject? element = visual; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is DataGridColumnHeader) return true;
            if (element is DataGrid) break;
        }
        return false;
    }

    private void ChannelMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var row = FollowedChannelsGrid.SelectedItem as FollowedChannelRow;
        menu.IsOpen = row is not null;
        ManageStreamMenu.Visibility = row is { Watching: true } ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in ManageStreamMenu.Items.OfType<MenuItem>())
            item.IsEnabled = item.Tag as string == "edge" ? row?.EdgeAvailable == true : row?.ChromeAvailable == true;
        AddOshiMenu.Items.Clear();
        if (row is null || RankChoices?.Invoke(row) is not { } choices)
        {
            AddOshiMenu.IsEnabled = false;
            return;
        }
        AddOshiMenu.IsEnabled = true;
        AddChoice("New rank", null);
        for (var index = 0; index < choices.Length; index++)
            AddChoice(choices[index].NumberedName(index), choices[index].Id);

        void AddChoice(string label, string? rank)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => AddOshiRequested?.Invoke(row, rank);
            AddOshiMenu.Items.Add(item);
        }
    }

    private void CopyLogin_Click(object sender, RoutedEventArgs e)
    {
        if (FollowedChannelsGrid.SelectedItem is FollowedChannelRow row) CopyLoginRequested?.Invoke(row.Login);
    }

    private async void WatchBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FollowedChannelRow row, Tag: string browser } button
            && WatchRequested is { } watch)
        {
            button.IsEnabled = false;
            try { await watch(row, browser, false); }
            finally { button.IsEnabled = true; }
        }
    }

    private async void ManageStream_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string browser } && FollowedChannelsGrid.SelectedItem is FollowedChannelRow row
            && WatchRequested is { } watch)
            await watch(row, browser, true);
    }

    private void EditSort_Click(object sender, RoutedEventArgs e) => EditSortRequested?.Invoke();
}

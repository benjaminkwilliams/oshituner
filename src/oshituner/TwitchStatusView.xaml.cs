using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace oshituner;

public partial class TwitchStatusView : UserControl
{
    private readonly FollowedChannelViewModel _viewModel = new();
    private IReadOnlyList<FollowedChannelRow>? _renderedRows;
    private int _refreshMinutes;

    internal Action? RefreshRequested { get; set; }
    internal Action<int>? IntervalChanged { get; set; }
    internal Action? EditSortRequested { get; set; }
    internal Func<FollowedChannelRow, string, Task>? WatchRequested { get; set; }
    internal Action<string>? CopyLoginRequested { get; set; }

    public TwitchStatusView()
    {
        InitializeComponent();
        DataContext = _viewModel;
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

        if (!ReferenceEquals(_renderedRows, session.Channels))
        {
            var selected = (FollowedChannelsGrid.SelectedItem as FollowedChannelRow)?.ChannelId;
            _renderedRows = session.Channels;
            _viewModel.FollowedChannels.Clear();
            foreach (var row in _renderedRows) _viewModel.FollowedChannels.Add(row);
            if (selected is not null)
                FollowedChannelsGrid.SelectedItem = _renderedRows.FirstOrDefault(row => row.ChannelId == selected);
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
        if (e.OriginalSource is DependencyObject element)
            FollowedChannelsGrid.SelectedItem = (ItemsControl.ContainerFromElement(FollowedChannelsGrid, element) as DataGridRow)?.Item;
    }

    private void ChannelMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            menu.IsOpen = FollowedChannelsGrid.SelectedItem is FollowedChannelRow;
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
            try { await watch(row, browser); }
            finally { button.IsEnabled = true; }
        }
    }

    private void EditSort_Click(object sender, RoutedEventArgs e) => EditSortRequested?.Invoke();
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace oshituner;

public partial class TwitchStatusView : UserControl
{
    private readonly FollowedChannelViewModel _viewModel = new();
    private IReadOnlyList<FollowedChannelRow>? _renderedRows;

    internal Action? RefreshRequested { get; set; }
    internal Action<int>? IntervalChanged { get; set; }
    internal Action? EditSortRequested { get; set; }
    internal Action<FollowedChannelRow>? OpenChannelRequested { get; set; }
    internal Action<string>? CopyLoginRequested { get; set; }

    public TwitchStatusView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        AutoRefresh.ItemsSource = TunerSession.RefreshIntervals;
    }

    internal void Render(TunerSession session)
    {
        var connected = session.UserId is not null;
        ListToolbar.Visibility = connected && session.ActivationUri is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = !session.Busy;

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
        AutoRefresh.SelectedItem = session.AutoRefreshMinutes;
    }

    internal void FocusGrid() => FollowedChannelsGrid.Focus();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void AutoRefresh_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.SelectedItem is int minutes) IntervalChanged?.Invoke(minutes);
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

    private void OpenChannel_Click(object sender, RoutedEventArgs e)
    {
        if (FollowedChannelsGrid.SelectedItem is FollowedChannelRow row) OpenChannelRequested?.Invoke(row);
    }

    private void EditSort_Click(object sender, RoutedEventArgs e) => EditSortRequested?.Invoke();
}

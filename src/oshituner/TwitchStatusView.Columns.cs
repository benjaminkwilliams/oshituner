using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace oshituner;

public partial class TwitchStatusView
{
    private readonly ICollectionView _columnView;
    private readonly Dictionary<string, string> _filters = new(StringComparer.Ordinal);
    private ContextMenu? _headerMenu;
    private DataGridColumn? _headerColumn;
    private DataGridColumn? _editorColumn;
    private bool _finding;

    private bool MatchesFilters(object item) => item is FollowedChannelRow row &&
        _filters.All(filter => Contains(ColumnText(row, filter.Key), filter.Value));

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.CurrentCultureIgnoreCase);

    private static string ColumnText(FollowedChannelRow row, string column) => column switch
    {
        nameof(FollowedChannelRow.RankOrder) => row.RankName,
        nameof(FollowedChannelRow.Status) => row.Status,
        nameof(FollowedChannelRow.WatchStatus) => row.WatchStatus,
        nameof(FollowedChannelRow.SubscriptionStatus) => row.SubscriptionStatus,
        nameof(FollowedChannelRow.DisplayName) => row.DisplayName,
        nameof(FollowedChannelRow.GameName) => row.GameName,
        nameof(FollowedChannelRow.StreamTitle) => row.StreamTitle,
        nameof(FollowedChannelRow.ViewerCount) => row.ViewerCount.ToString("N0", CultureInfo.CurrentCulture),
        nameof(FollowedChannelRow.FollowedAt) => row.FollowedAt?.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "",
        _ => ""
    };

    private void ColumnHeader_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridColumnHeader { Column: not null } header)
            header.ContextMenu = _headerMenu ??= CreateHeaderMenu();
    }

    private ContextMenu CreateHeaderMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += HeaderMenu_Opened;
        menu.Items.Add(Option("_Filter…", FilterColumn_Click));
        menu.Items.Add(Option("_Find…", FindColumn_Click));
        var sort = new MenuItem { Header = "_Sort" };
        sort.Items.Add(Option("_Ascending", SortColumn_Click));
        sort.Items.Add(Option("_Descending", SortColumnDescending_Click));
        sort.Items.Add(Option("Use _default order", ResetColumnSort_Click));
        menu.Items.Add(sort);
        menu.Items.Add(new Separator());
        menu.Items.Add(Option("_Clear column filter", ClearColumnFilter_Click));
        menu.Items.Add(Option("Clear _all filters", ClearAllFilters_Click));
        return menu;

        static MenuItem Option(string label, RoutedEventHandler action)
        {
            var item = new MenuItem { Header = label };
            item.Click += action;
            return item;
        }
    }

    private void HeaderMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        _headerColumn = (menu.PlacementTarget as DataGridColumnHeader)?.Column;
        if (_headerColumn is not { SortMemberPath.Length: > 0 }) menu.IsOpen = false;
    }

    private void FilterColumn_Click(object sender, RoutedEventArgs e) => OpenColumnEditor(finding: false);
    private void FindColumn_Click(object sender, RoutedEventArgs e) => OpenColumnEditor(finding: true);

    private void OpenColumnEditor(bool finding)
    {
        if (_headerColumn is not { } column) return;
        _editorColumn = column;
        _finding = finding;
        ColumnEditorLabel.Content = $"{(finding ? "Find in" : "Filter")} {column.Header}";
        ColumnQuery.Text = finding ? "" : _filters.GetValueOrDefault(column.SortMemberPath, "");
        ColumnApply.Content = finding ? "Find next" : "Apply filter";
        ColumnFeedback.Text = "";
        ColumnEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            ColumnQuery.Focus();
            ColumnQuery.SelectAll();
        });
    }

    private void ColumnApply_Click(object sender, RoutedEventArgs e) => ApplyColumnQuery();

    private void ColumnQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyColumnQuery();
        else if (e.Key == Key.Escape) CloseColumnEditor();
        else return;
        e.Handled = true;
    }

    private void ApplyColumnQuery()
    {
        if (_editorColumn is not { } column) return;
        var query = ColumnQuery.Text.Trim();
        if (!_finding)
        {
            if (query.Length == 0) _filters.Remove(column.SortMemberPath);
            else _filters[column.SortMemberPath] = query;
            _columnView.Refresh();
            ColumnFeedback.Text = $"{FollowedChannelsGrid.Items.Count} shown";
            return;
        }

        var rows = _columnView.Cast<FollowedChannelRow>().ToArray();
        var selected = Array.IndexOf(rows, FollowedChannelsGrid.SelectedItem);
        for (var offset = 1; query.Length != 0 && offset <= rows.Length; offset++)
        {
            var row = rows[(selected + offset) % rows.Length];
            if (!Contains(ColumnText(row, column.SortMemberPath), query)) continue;
            FollowedChannelsGrid.SelectedItem = row;
            FollowedChannelsGrid.ScrollIntoView(row, column);
            ColumnFeedback.Text = $"Found {row.DisplayName}";
            return;
        }
        ColumnFeedback.Text = query.Length == 0 ? "Enter text to find" : "No match in visible rows";
    }

    private void CloseColumnEditor_Click(object sender, RoutedEventArgs e) => CloseColumnEditor();

    private void CloseColumnEditor()
    {
        ColumnEditor.Visibility = Visibility.Collapsed;
        _editorColumn = null;
        FocusGrid();
    }

    private void ClearColumnFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_headerColumn is not { } column || !_filters.Remove(column.SortMemberPath)) return;
        if (!_finding && _editorColumn == column) CloseColumnEditor();
        _columnView.Refresh();
    }

    private void ClearAllFilters_Click(object sender, RoutedEventArgs e)
    {
        _filters.Clear();
        if (!_finding && _editorColumn is not null) CloseColumnEditor();
        _columnView.Refresh();
    }

    private void SortColumn_Click(object sender, RoutedEventArgs e) => SetColumnSort(ListSortDirection.Ascending);
    private void SortColumnDescending_Click(object sender, RoutedEventArgs e) => SetColumnSort(ListSortDirection.Descending);
    private void ResetColumnSort_Click(object sender, RoutedEventArgs e) => SetColumnSort(null);

    private void SetColumnSort(ListSortDirection? direction)
    {
        if (_headerColumn is not { } column) return;
        using (_columnView.DeferRefresh())
        {
            _columnView.SortDescriptions.Clear();
            foreach (var current in FollowedChannelsGrid.Columns) current.SortDirection = null;
            if (direction is { } order)
            {
                _columnView.SortDescriptions.Add(new(column.SortMemberPath, order));
                column.SortDirection = order;
            }
        }
    }
}

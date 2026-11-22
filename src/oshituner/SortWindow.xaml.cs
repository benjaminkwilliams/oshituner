using System.Collections.ObjectModel;
using System.Windows;

namespace oshituner;

public partial class SortWindow : Window
{
    private readonly ObservableCollection<GroupOption> _groups = [];
    private static readonly SecondaryOption[] SecondaryOptions =
    [
        new(SecondarySort.Viewers, "Viewer count"),
        new(SecondarySort.ChannelName, "Channel name"),
        new(SecondarySort.MostRecentlyFollowed, "Most recently followed")
    ];
    internal GridSort Selection { get; private set; }

    internal SortWindow(GridSort sort)
    {
        Selection = sort;
        InitializeComponent();
        Groups.ItemsSource = _groups;
        Secondary.ItemsSource = SecondaryOptions;
        LoadSort(sort);
    }

    private void LoadSort(GridSort sort)
    {
        _groups.Clear();
        foreach (var group in sort.Groups)
        {
            var title = group switch
            {
                GridGroup.SubbedAndFollowed => "Subbed and followed",
                GridGroup.Online => "Online",
                GridGroup.SubbedOnly => "Subbed only",
                _ => "Followed"
            };
            _groups.Add(new(group, title));
        }
        Groups.SelectedIndex = 0;
        Secondary.SelectedItem = SecondaryOptions.Single(option => option.Value == sort.Secondary);
    }

    private void Up_Click(object sender, RoutedEventArgs args) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs args) => Move(1);
    private void Reset_Click(object sender, RoutedEventArgs args) => LoadSort(GridSort.Default());

    private void Move(int offset)
    {
        var index = Groups.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _groups.Count) return;
        _groups.Move(index, target);
        Groups.SelectedIndex = target;
    }

    private void Save_Click(object sender, RoutedEventArgs args)
    {
        if (Secondary.SelectedItem is not SecondaryOption option) return;
        Selection = new(_groups.Select(group => group.Value).ToArray(), option.Value);
        DialogResult = true;
    }

    private sealed record GroupOption(GridGroup Value, string Title);
    private sealed record SecondaryOption(SecondarySort Value, string Title);
}

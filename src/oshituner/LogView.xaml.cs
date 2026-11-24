using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace oshituner;

public partial class LogView : UserControl
{
    private INotifyCollectionChanged? _entries;
    private bool _followLatest = true;
    private bool _scrollQueued;

    public LogView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => QueueScroll();
    }

    internal void Attach(LaunchLog log)
    {
        if (_entries is not null) CollectionChangedEventManager.RemoveHandler(_entries, EntriesChanged);
        _entries = log.Entries;
        CollectionChangedEventManager.AddHandler(_entries, EntriesChanged);
        LogEntries.ItemsSource = log.Entries;
        LogPath.Text = log.FilePath;
    }

    internal void FocusEntries()
    {
        LogEntries.Focus();
        _followLatest = true;
        QueueScroll();
    }

    private void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset) QueueScroll();
    }

    private void LogEntries_ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (args.ExtentHeightChange == 0 && args.VerticalChange != 0)
            _followLatest = args.ExtentHeight - args.ViewportHeight - args.VerticalOffset < 0.001;
    }

    private void QueueScroll()
    {
        if (!_followLatest || _scrollQueued || !IsVisible) return;
        _scrollQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _scrollQueued = false;
            if (_followLatest && IsVisible && LogEntries.Items.Count > 0)
                LogEntries.ScrollIntoView(LogEntries.Items[LogEntries.Items.Count - 1]);
        }, DispatcherPriority.Loaded);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogEntries.Items.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, LogEntries.Items.Cast<LaunchLogEntry>().Select(entry => entry.Text)));
        }
        catch (ExternalException)
        {
            LogPath.ToolTip = "Clipboard is busy. Try copying again.";
        }
    }
}

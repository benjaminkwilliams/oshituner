using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace oshituner;

public partial class LogView : UserControl
{
    public LogView() => InitializeComponent();

    internal void Attach(LaunchLog log)
    {
        LogEntries.ItemsSource = log.Entries;
        LogPath.Text = log.FilePath;
    }

    internal void FocusEntries()
    {
        LogEntries.Focus();
        if (LogEntries.Items.Count > 0)
            LogEntries.ScrollIntoView(LogEntries.Items[LogEntries.Items.Count - 1]);
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

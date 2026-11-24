using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace oshituner;

internal static class BrowserPairingApproval
{
    internal static bool Confirm(string browser, string previous, string next, Window? owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible && window.IsActive);

        var dialog = new Window
        {
            Title = "Confirm browser pairing",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner is not null) dialog.Owner = owner;

        var content = new StackPanel { Margin = new Thickness(18) };
        dialog.Content = content;
        content.Children.Add(new TextBlock
        {
            Text = $"Move {browser} to this copy of Oshi Tuner?",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        });
        foreach (var line in new[] { $"Current installation: {previous}", $"New installation: {next}" })
            content.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        content.Children.Add(buttons);
        var approve = new Button { Content = "Move pairing", MinWidth = 105, Margin = new Thickness(0, 0, 8, 0) };
        approve.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(approve);
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, MinWidth = 75 };
        buttons.Children.Add(cancel);

        var timer = new DispatcherTimer(DispatcherPriority.Normal, dialog.Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => dialog.Close();
        using var registration = token.Register(() => dialog.Dispatcher.InvokeAsync(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        }));
        timer.Start();
        try { return dialog.ShowDialog() == true && !token.IsCancellationRequested; }
        finally { timer.Stop(); }
    }
}

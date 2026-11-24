using System.Windows;

namespace oshituner;

internal sealed record DesktopLaunchOptions(bool StartInTray = false, bool StartOshiWatch = false,
    bool McpEnabled = true, bool RpcEnabled = true);

public partial class LaunchOptionsWindow : Window
{
    internal DesktopLaunchOptions Options => new(Tray.IsChecked == true, Watch.IsChecked == true,
        Mcp.IsChecked == true, Rpc.IsChecked == true);

    internal LaunchOptionsWindow(DesktopLaunchOptions options)
    {
        InitializeComponent();
        Windowed.IsChecked = !options.StartInTray;
        Tray.IsChecked = options.StartInTray;
        Watch.IsChecked = options.StartOshiWatch;
        Mcp.IsChecked = options.McpEnabled;
        Rpc.IsChecked = options.RpcEnabled;
    }

    private void Launch_Click(object sender, RoutedEventArgs args) => DialogResult = true;
}

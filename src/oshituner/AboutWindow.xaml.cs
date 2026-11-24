using System.Windows;

namespace oshituner;

public partial class AboutWindow : Window
{
    private readonly TunerControl _control;
    private readonly CancellationTokenSource _lifetime = new();

    internal AboutWindow(TunerControl control)
    {
        _control = control;
        InitializeComponent();
        DataContext = AppInformation.Current;
        Closed += (_, _) => _lifetime.Cancel();
        Loaded += LoadRuntimeInfo;
    }

    private async void LoadRuntimeInfo(object sender, RoutedEventArgs args)
    {
        Loaded -= LoadRuntimeInfo;
        var token = _lifetime.Token;
        var targets = BrowserRegistration.Targets.ToArray();
        var rows = targets.Select(target => TunerControl.ExtensionStatus(target, "Pending")).ToArray();
        RenderRuntimeInfo(rows);
        try
        {
            while (true)
            {
                await Task.WhenAll(targets.Select(async (target, index) =>
                {
                    var info = await _control.GetExtensionInfoAsync(target, token);
                    if (token.IsCancellationRequested || info == rows[index]) return;
                    rows[index] = info;
                    RenderRuntimeInfo(rows);
                }));
                RenderRuntimeInfo(rows);
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _lifetime.Dispose(); }
    }

    private void RenderRuntimeInfo(BrowserExtensionInfo[] rows)
    {
        var info = _control.AboutSnapshot(rows);
        var launch = info.Launch!;
        var activity = info.ProtocolActivity!;
        ExtensionGrid.ItemsSource = rows.ToArray();
        LaunchSummary.Text = $"{launch.Mode} · {launch.Source}\n" +
            $"Oshi Watch at launch: {(launch.StartOshiWatch ? "On" : "Paused")}";
        ProtocolSummary.Text = $"MCP: {(launch.McpEnabled ? $"Enabled ({activity.ActiveMcpConnections} active)" : "Disabled")}\n" +
            $"JSON-RPC: {(launch.RpcEnabled ? "Enabled" : "Disabled")}" +
            (activity.LastRpcRequest is { } request ? $" · Last request {request.ToLocalTime():g}" : "");
    }

    private void Repository_Click(object sender, RoutedEventArgs args) => OpenLink("repository");
    private void Donations_Click(object sender, RoutedEventArgs args) => OpenLink("donations");
    private void License_Click(object sender, RoutedEventArgs args) => OpenLink("license");
    private void McpLicense_Click(object sender, RoutedEventArgs args) => OpenLink("mcp_license");
    private void ImageDecoderLicense_Click(object sender, RoutedEventArgs args) => OpenLink("image_decoder_license");
    private void Close_Click(object sender, RoutedEventArgs args) => _control.CloseAbout();

    private void OpenLink(string link)
    {
        try { _control.OpenAboutLink(link); LinkStatus.Text = ""; }
        catch (InvalidOperationException exception) { LinkStatus.Text = exception.Message; }
    }
}

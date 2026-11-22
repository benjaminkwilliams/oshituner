using System.Windows;

namespace oshituner;

public partial class AboutWindow : Window
{
    private readonly TunerControl _control;

    internal AboutWindow(TunerControl control)
    {
        _control = control;
        InitializeComponent();
        DataContext = AppInformation.Current;
    }

    private void Repository_Click(object sender, RoutedEventArgs args) => OpenLink("repository");
    private void Donations_Click(object sender, RoutedEventArgs args) => OpenLink("donations");
    private void License_Click(object sender, RoutedEventArgs args) => OpenLink("license");
    private void McpLicense_Click(object sender, RoutedEventArgs args) => OpenLink("mcp_license");
    private void Close_Click(object sender, RoutedEventArgs args) => _control.CloseAbout();

    private void OpenLink(string link)
    {
        try { _control.OpenAboutLink(link); LinkStatus.Text = ""; }
        catch (InvalidOperationException exception) { LinkStatus.Text = exception.Message; }
    }
}

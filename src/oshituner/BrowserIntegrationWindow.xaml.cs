using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace oshituner;

public partial class BrowserIntegrationWindow : Window
{
    private readonly BrowserRegistration _registration;
    private readonly bool _installerSetup;

    internal BrowserIntegrationWindow(BrowserRegistration registration, bool installerSetup = false)
    {
        InitializeComponent();
        _registration = registration;
        _installerSetup = installerSetup;
        if (_installerSetup) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Refresh();
        Activated += (_, _) => Refresh();
    }

    internal void Refresh()
    {
        BrowserList.ItemsSource = _registration.GetPairings().Select(pairing =>
            new BrowserRow(pairing.Browser, pairing.DisplayName, pairing.State switch
            {
                "paired_here" => "Paired with this installation.",
                "paired_elsewhere" => _installerSetup
                    ? $"Paired with {pairing.InstallPath}. To switch, use Settings > Browser Integration."
                    : $"Paired with {pairing.InstallPath}.",
                _ => "Unpaired."
            }, pairing.State == "paired_elsewhere" ? "Switch to this install" : "Pair",
            pairing.State == "unpaired" || (!_installerSetup && pairing.State == "paired_elsewhere"),
            !_installerSetup && pairing.State == "paired_here")).ToArray();
    }

    private void Pair_Click(object sender, RoutedEventArgs args) => Update((string)((Button)sender).Tag, pair: true);

    private void Unpair_Click(object sender, RoutedEventArgs args) => Update((string)((Button)sender).Tag, pair: false);

    private void Update(string browser, bool pair)
    {
        try
        {
            var target = BrowserRegistration.GetTarget(browser);
            if (pair)
            {
                var previous = _registration.GetPairings().Single(pairing => pairing.Browser == browser);
                if (previous.State == "paired_elsewhere")
                {
                    if (_installerSetup) throw new InvalidOperationException("Switch the existing pairing in Browser Integration settings.");
                    var choice = MessageBox.Show(this, $"Move {target.DisplayName} from {previous.InstallPath} to this installation?",
                        Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (choice != MessageBoxResult.Yes) return;
                }
                _registration.Pair(target, replace: previous.State == "paired_elsewhere");
            }
            else
                _registration.Unpair(target);
            Message.Text = "";
            Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message.Text = exception.Message;
        }
    }

    private void Instructions_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/benjaminkwilliams/oshituner/blob/main/docs/browser-integration.md")
                { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Message.Text = "Could not open setup instructions. Check your default browser.";
        }
    }

    private sealed record BrowserRow(string Browser, string DisplayName, string Status,
        string PairLabel, bool CanPair, bool CanUnpair);
}

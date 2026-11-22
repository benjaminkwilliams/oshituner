using System.IO;
using System.Windows;

namespace oshituner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Could not open this copy of Oshi Tuner. Close any running copy from the same folder and check your Windows profile permissions.",
                "Oshi Tuner", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

using System.Windows;

namespace oshituner;

public partial class ClientIdWindow : Window
{
    private readonly TunerControl _control;

    internal ClientIdWindow(TunerControl control, string currentId, bool canEdit)
    {
        _control = control;
        InitializeComponent();
        ClientIdTextBox.Text = currentId;
        SetEditable(canEdit);
        Loaded += (_, _) => ClientIdTextBox.Focus();
    }

    internal void SetEditable(bool canEdit)
    {
        ClientIdTextBox.IsReadOnly = !canEdit;
        ApplyClientIdButton.IsEnabled = canEdit;
        EditorMessage.Text = canEdit ? "Apply the ID, then select Connect Twitch."
            : "Finish the current operation and disconnect Twitch before changing the Client ID.";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _control.SetClientId("twitch", ClientIdTextBox.Text);
            _control.CloseClientId("twitch");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            EditorMessage.Text = ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _control.CloseClientId("twitch");
}

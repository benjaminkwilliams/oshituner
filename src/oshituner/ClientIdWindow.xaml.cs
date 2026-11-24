using System.Windows;

namespace oshituner;

public partial class ClientIdWindow : Window
{
    private readonly TunerControl _control;
    private readonly TunerSession _session;
    private bool _connectAfterSave;

    internal ClientIdWindow(TunerControl control, TunerSession session, bool connectAfterSave)
    {
        _control = control;
        _session = session;
        InitializeComponent();
        BuiltInChoice.IsEnabled = session.HasBuiltInClientId;
        BuiltInChoice.IsChecked = session.UsingBuiltInClientId && session.HasBuiltInClientId;
        CustomChoice.IsChecked = BuiltInChoice.IsChecked != true;
        ClientIdTextBox.Text = session.CustomClientId;
        if (connectAfterSave) RequestConnectAfterSave();
        SetEditable(!session.Busy);
    }

    internal void RequestConnectAfterSave()
    {
        _connectAfterSave = true;
        ApplyClientIdButton.Content = "Continue";
    }

    internal void SetEditable(bool canEdit)
    {
        ChoicePanel.IsEnabled = canEdit;
        ApplyClientIdButton.IsEnabled = canEdit;
        EditorMessage.Text = !canEdit ? "Finish the current Twitch operation before changing the Client ID."
            : _session.ClientIdOverridden ? "The environment override is active for this launch."
            : !_session.HasBuiltInClientId ? "This build requires your own public Twitch Client ID."
            : "";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var builtIn = BuiltInChoice.IsChecked == true;
            var needsAuthorization = _session.ClientChoiceNeedsAuthorization(builtIn, ClientIdTextBox.Text);
            if (needsAuthorization && MessageBox.Show(this,
                "Changing the Client ID requires you to authorize Oshi Tuner on Twitch again. Apply this change?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _session.SetClientId(ClientIdTextBox.Text, builtIn, needsAuthorization);
            _control.CloseClientId("twitch");
            if (_connectAfterSave) _control.Start("twitch", true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            EditorMessage.Text = ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _control.CloseClientId("twitch");
}

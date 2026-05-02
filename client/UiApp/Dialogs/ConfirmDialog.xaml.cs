using System.Windows;

namespace UiApp.Dialogs;

public sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string? confirmText = null)
    {
        confirmText ??= UiApp.Properties.Strings.Button_Delete;
        InitializeComponent();
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        var isDark = new Services.SettingsService().Load().ThemeMode == "Dark";
        MainWindow.ApplyDarkTitleBar(this, isDark);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

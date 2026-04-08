using System.Windows;

namespace UiApp.Dialogs;

public sealed partial class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string confirmText = "Удалить")
    {
        InitializeComponent();
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
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

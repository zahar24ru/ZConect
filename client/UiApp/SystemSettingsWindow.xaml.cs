using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using UiApp.Dialogs;
using UiApp.ViewModels;

namespace UiApp;

public partial class SystemSettingsWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public SystemSettingsWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Автосохранение при закрытии окна настроек.
        Vm?.SaveSettings();
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        Vm?.OpenLogFile();
    }

    private void ClearLogFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog("Удалить все файлы логов?") { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Vm?.ClearLogFiles();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SessionClient;
using UiApp.Services;
using UiApp.ViewModels;

namespace UiApp;

public partial class MainWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private RemoteScreenWindow? _remoteWindow;
    private SystemSettingsWindow? _systemSettingsWindow;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        var sessionApiClient = new SessionApiClient(new HttpClient());
        DataContext = new MainViewModel(new SettingsService(), new LogService(), sessionApiClient);

        if (DataContext is MainViewModel vm)
        {
            vm.RemoteScreenWindowRequested += OpenOrActivateRemoteScreenWindow;
        }

        Closing += OnClosingAsync;
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            // Shutdown is already in progress; ignore repeated clicks.
            e.Cancel = true;
            return;
        }

        _closing = true;
        e.Cancel = true;
        IsEnabled = false;

        try
        {
            // Close/hide remote window first to stop UI event pumping.
            if (_remoteWindow is not null)
            {
                try
                {
                    _remoteWindow.AllowRealClose();
                    _remoteWindow.Close();
                }
                catch
                {
                    // ignore
                }
            }
            if (_systemSettingsWindow is not null)
            {
                try { _systemSettingsWindow.Close(); } catch { /* ignore */ }
            }

            if (Vm is not null)
            {
                // Never hang forever on close: wait bounded time for graceful shutdown.
                var shutdownTask = Vm.ShutdownAsync();
                var completed = await Task.WhenAny(shutdownTask, Task.Delay(3000)) == shutdownTask;
                if (!completed)
                {
                    // Last-resort sync cleanup attempt.
                    try { Vm.Shutdown(); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            // Now allow the window to actually close.
            Closing -= OnClosingAsync;
            Application.Current.Shutdown();
        }
        catch
        {
            // Last-resort: do not leave zombie process in memory.
            Environment.Exit(0);
        }
    }

    private void OpenOrActivateRemoteScreenWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_remoteWindow is null || !_remoteWindow.IsVisible)
            {
                _remoteWindow = new RemoteScreenWindow
                {
                    Owner = this,
                    DataContext = DataContext
                };
                _remoteWindow.Show();
            }
            else
            {
                _remoteWindow.Activate();
                if (_remoteWindow.WindowState == WindowState.Minimized)
                {
                    _remoteWindow.WindowState = WindowState.Normal;
                }
            }
        });
    }

    private void MenuSystem_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_systemSettingsWindow is null || !_systemSettingsWindow.IsVisible)
            {
                _systemSettingsWindow = new SystemSettingsWindow
                {
                    Owner = this,
                    DataContext = DataContext
                };
                _systemSettingsWindow.Show();
            }
            else
            {
                _systemSettingsWindow.Activate();
                if (_systemSettingsWindow.WindowState == WindowState.Minimized)
                {
                    _systemSettingsWindow.WindowState = WindowState.Normal;
                }
            }
        });
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void MenuToolsStub1_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Раздел инструментов будет добавлен в следующих версиях.", "Инструменты", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuToolsStub2_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Раздел инструментов будет добавлен в следующих версиях.", "Инструменты", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Справка будет добавлена в следующих версиях.", "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs.log");
            if (!File.Exists(logsPath))
            {
                MessageBox.Show(this, "Файл логов пока не создан.", "Логи", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = logsPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть лог: " + ex.Message, "Логи", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}

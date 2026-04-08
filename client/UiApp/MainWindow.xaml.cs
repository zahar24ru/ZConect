using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FileTransfer;
using SessionClient;
using UiApp.Services;
using UiApp.ViewModels;

namespace UiApp;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    private MainViewModel? Vm => DataContext as MainViewModel;
    private RemoteScreenWindow? _remoteWindow;
    private SystemSettingsWindow? _systemSettingsWindow;
    private bool _closing;
    private bool _forceClose; // true = exit app, false = minimize to tray
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

    public MainWindow()
    {
        InitializeComponent();
        var sessionApiClient = new SessionApiClient(new HttpClient());
        DataContext = new MainViewModel(new SettingsService(), new LogService(), sessionApiClient, new AddressBookService());

        if (DataContext is MainViewModel vm)
        {
            vm.RemoteScreenWindowRequested += OpenOrActivateRemoteScreenWindow;
            vm.FileTransferWindowRequested += OpenFileTransferWindow;
            vm.ViewerConnectedNotification += OnViewerConnected;
        }

        InitNotifyIcon();

        Closing += OnClosingAsync;
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm2)
                await vm2.InitializeOnLoadAsync();
        };
    }

    private void InitNotifyIcon()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
        var icon = iconStream is not null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application;

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "ZConect",
            Visible = false
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ForceClose());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            _notifyIcon!.Visible = false;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ForceClose()
    {
        Dispatcher.Invoke(() =>
        {
            _forceClose = true;
            _notifyIcon?.Dispose();
            _notifyIcon = null;
            Close();
        });
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        // Minimize to tray instead of closing (unless force close from tray menu, Меню→Выход, or setting disabled).
        if (!_forceClose && Vm?.MinimizeToTray == true)
        {
            e.Cancel = true;
            Hide();
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(2000, "ZConect", "Приложение свёрнуто в трей", System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }

        _closing = true;
        e.Cancel = true;
        IsEnabled = false;

        try
        {
            if (_remoteWindow is not null)
            {
                try
                {
                    _remoteWindow.AllowRealClose();
                    _remoteWindow.Close();
                }
                catch { /* ignore */ }
            }
            if (_systemSettingsWindow is not null)
            {
                try { _systemSettingsWindow.Close(); } catch { /* ignore */ }
            }

            if (Vm is not null)
            {
                var shutdownTask = Vm.ShutdownAsync();
                var completed = await Task.WhenAny(shutdownTask, Task.Delay(3000)) == shutdownTask;
                if (!completed)
                {
                    try { Vm.Shutdown(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            Closing -= OnClosingAsync;
            Application.Current.Shutdown();
        }
        catch { /* ignore */ }
        Environment.Exit(0);
    }

    private void OpenOrActivateRemoteScreenWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_remoteWindow is null || !_remoteWindow.IsVisible)
            {
                _remoteWindow = new RemoteScreenWindow
                {
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

    private void OpenFileTransferWindow(FileTransferService fileTransferService, UiApp.Models.IncomingSaveDirHolder incomingSaveDirHolder, UiApp.Models.OutgoingTargetDirHolder outgoingTargetDirHolder, MainViewModel mainViewModel)
    {
        Dispatcher.Invoke(() =>
        {
            var w = new FileTransferWindow(fileTransferService, incomingSaveDirHolder, outgoingTargetDirHolder, mainViewModel) { Owner = this };
            w.Show();
        });
    }

    private void OnViewerConnected()
    {
        Dispatcher.Invoke(() =>
        {
            // Play system notification sound.
            SystemSounds.Asterisk.Play();

            // Show Windows native notification via tray icon.
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(3000, "ZConnect", "Зритель подключился к вашей сессии", System.Windows.Forms.ToolTipIcon.Info);
                // Hide tray icon after balloon if window is visible (not minimized to tray).
                if (IsVisible)
                {
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    timer.Tick += (_, _) => { timer.Stop(); if (IsVisible && _notifyIcon is not null) _notifyIcon.Visible = false; };
                    timer.Start();
                }
            }

            // Flash taskbar if window is not focused.
            if (!IsActive)
            {
                var helper = new WindowInteropHelper(this);
                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = helper.Handle,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = 5,
                    dwTimeout = 0
                };
                FlashWindowEx(ref info);
            }
        });
    }

    private void SessionCode_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (Vm is null) return;
        var login = Vm.LoginCode?.Trim() ?? string.Empty;
        var pass = Vm.PassCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(pass)) return;

        try
        {
            Clipboard.SetText($"Логин: {login}{Environment.NewLine}Пароль: {pass}");
            ShowToast("Логин и пароль скопированы");
        }
        catch { /* ignore */ }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = TimeSpan.FromSeconds(2)
        };
        ToastOverlay.BeginAnimation(OpacityProperty, null);
        ToastOverlay.Opacity = 0;
        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Children.Add(fadeOut);
        Storyboard.SetTarget(fadeIn, ToastOverlay);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, ToastOverlay);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
        sb.Begin();
    }

    private void ShowViewerToast()
    {
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400))
        {
            BeginTime = TimeSpan.FromSeconds(3)
        };
        ViewerToast.BeginAnimation(OpacityProperty, null);
        ViewerToast.Opacity = 0;
        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Children.Add(fadeOut);
        Storyboard.SetTarget(fadeIn, ViewerToast);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, ViewerToast);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
        sb.Begin();
    }

    private void MenuSystem_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_systemSettingsWindow is null || !_systemSettingsWindow.IsVisible)
            {
                _systemSettingsWindow = new SystemSettingsWindow
                {
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
        ForceClose();
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

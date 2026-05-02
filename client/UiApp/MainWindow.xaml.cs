using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FileTransfer;
using SessionClient;
using UiApp.Properties;
using UiApp.Services;
using UiApp.ViewModels;

namespace UiApp;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

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

    /// <summary>WM_TASKBARCREATED — broadcast when Explorer restarts (tray icons lost).</summary>
    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

    private MainViewModel? Vm => DataContext as MainViewModel;
    private RemoteScreenWindow? _remoteWindow;
    private SystemSettingsWindow? _systemSettingsWindow;
    private TelemetryService? _telemetryService;
    private Services.UpdateChecker? _updateChecker;
    private Services.PresenceService? _presenceService;
    private bool _closing;
    private bool _forceClose; // true = exit app, false = minimize to tray
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Threading.DispatcherTimer? _viewerNotifyTimer;

    public MainWindow()
    {
        InitializeComponent();
        Title = "ZConnect";
        ContentRendered += (_, _) => ApplyWindowTitle("ZConnect");
        SourceInitialized += (_, _) => ApplyWindowTitle("ZConnect");
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => ShowScrim(HasOpenModalOwnedByUs());
        Activated += (_, _) => ShowScrim(HasOpenModalOwnedByUs());
        var ver = typeof(MainWindow).Assembly.GetName().Version;
        BannerVersionText.Text = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
        var logService = new LogService();
        // MaxResponseContentBufferSize cap — защита от malicious/buggy server'а который
        // присылает huge JSON (например 100 MB в turn_servers массиве). Все endpoints
        // у нас payload <100 KB (session JSON, presence, status), 1 MB — generous cap.
        // Audit fix 2026-04-24 (M2): явный timeout 30 сек на всех API calls —
        // без этого default 100 сек держит thread on hung server (half-open TCP
        // after TLS renegotiation glitch, network change). Same client reused
        // SessionApiClient / PresenceService / UpdateChecker / TelemetryService.
        var httpClient = new HttpClient
        {
            MaxResponseContentBufferSize = 1024 * 1024,
            Timeout = TimeSpan.FromSeconds(30)
        };
        var settingsService = new SettingsService();
        var settings = settingsService.Load();

        // Remove original LightColors + AppStyles from App.xaml MergedDictionaries.
        // They were needed for InitializeComponent parsing; our combined override takes over.
        var dicts = Application.Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source;
            if (src != null && (src.ToString().Contains("Colors") || src.ToString().Contains("AppStyles")))
                dicts.RemoveAt(i);
        }

        _isDarkTheme = settings.ThemeMode == "Dark";
        ApplyTheme(_isDarkTheme);
        RestoreWindowRect(this, settings.MainWindowRect);
        var sessionApiClient = new SessionApiClient(httpClient, msg => logService.Warn("SessionAPI", msg));
        DataContext = new MainViewModel(settingsService, logService, sessionApiClient, new AddressBookService());

        // Start telemetry heartbeat (fire-and-forget, never fails).
        _telemetryService = new TelemetryService(httpClient, settings.ServerApiBaseUrl, settings.MachineId,
            msg => logService.Debug("Telemetry", msg));
        _telemetryService.Start();

        // Auto-update checker — periodic poll сервера для check новой версии.
        // User может отключить проверку через Settings → AutoUpdateCheckEnabled=false.
        if (settings.AutoUpdateCheckEnabled)
        {
            _updateChecker = new Services.UpdateChecker(httpClient, settings.ServerApiBaseUrl,
                msg => logService.Debug("Update", msg));
            _updateChecker.UpdateAvailable += (_, info) =>
            {
                // Marshal to UI thread — event from Timer callback.
                Dispatcher.BeginInvoke(() =>
                {
                    if (DataContext is MainViewModel viewModel)
                    {
                        viewModel.LatestVersionText = info.LatestVersion;
                        viewModel.UpdateDownloadUrl = info.DownloadUrl;
                        viewModel.UpdateReleaseNotes = info.ReleaseNotes;
                        viewModel.UpdateSha256 = info.Sha256;
                        viewModel.IsUpdateAvailable = true;
                    }
                });
            };
        }

        // Live presence — опрашивает server /api/v1/presence каждые 30 сек
        // чтобы показать 🟢/🔴/⚪ dot на contact avatars в Address Book / Recent.
        _presenceService = new Services.PresenceService(httpClient, settings.ServerApiBaseUrl,
            msg => logService.Debug("Presence", msg));

        if (DataContext is MainViewModel vm)
        {
            vm.RemoteScreenWindowRequested += OpenOrActivateRemoteScreenWindow;
            vm.RemoteScreenWindowShouldHide += HideRemoteScreenWindow;
            vm.FileTransferWindowRequested += OpenFileTransferWindow;
            vm.ViewerConnectedNotification += OnViewerConnected;
            // Presence: VM даёт getter logins (Contacts + RecentContacts), service poll'ит.
            _presenceService.GetLogins = () =>
                vm.Contacts.Where(c => !string.IsNullOrEmpty(c.LoginCode)).Select(c => c.LoginCode);
            _presenceService.StateChanged += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    foreach (var c in vm.Contacts)
                    {
                        if (!string.IsNullOrEmpty(c.LoginCode))
                            c.Presence = _presenceService.GetState(c.LoginCode);
                    }
                });
            };
        }

        InitNotifyIcon();

        // Auto-save window rect on move/resize (debounced 500ms). Nuclear ForceClose
        // не вызывает OnClosingAsync и поэтому не может сохранить rect при exit —
        // debounce'имый save держит файл settings актуальным всё время.
        LocationChanged += OnMainWindowRectChanged;
        SizeChanged += (_, _) => OnMainWindowRectChanged(this, EventArgs.Empty);

        Closing += OnClosingAsync;
        Loaded += async (_, _) =>
        {
            // Set Title again after window is loaded — some implicit styles may
            // clear it during parsing. Re-apply with Win32 fallback here.
            ApplyWindowTitle("ZConnect");

            // Startup log here (not in constructor) — second instance is killed by
            // single-instance mutex in App.OnStartup before Loaded fires, so this
            // only logs for the surviving primary instance.
            logService.Info("UiApp", $"startup version={typeof(MainWindow).Assembly.GetName().Version} pid={Environment.ProcessId} args=[{string.Join(", ", Environment.GetCommandLineArgs().Skip(1))}]");

            // Hook WndProc to handle TaskbarCreated (Explorer restart → re-show tray icon).
            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            hwndSource?.AddHook(WndProc);

            // Show onboarding overlay on first launch (когда OnboardingCompleted=false).
            var currentSettings = settingsService.Load();
            if (!currentSettings.OnboardingCompleted)
            {
                ShowOnboarding();
            }

            var args = Environment.GetCommandLineArgs();
            var launchedByService = args.Any(a => a.Equals("--from-service", StringComparison.OrdinalIgnoreCase));
            var autoStart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

            // Start minimized to tray when launched by service OR via Registry autostart.
            if (launchedByService || autoStart)
            {
                await Task.Delay(500);
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
                Hide();
                // Show tray icon with retry — Explorer may not be ready after reboot.
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 30; i++) // retry for up to 30 seconds
                    {
                        await Task.Delay(1000);
                        try
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (_notifyIcon is not null)
                                {
                                    _notifyIcon.Visible = false;
                                    _notifyIcon.Visible = true;
                                }
                            });
                            // Check if taskbar exists (Shell_TrayWnd window)
                            if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                                break;
                        }
                        catch { }
                    }
                });
            }

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
            Text = "ZConnect",
            Visible = false
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Strings.Tray_Open, null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Strings.Tray_Exit, null, (_, _) => ForceClose());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

        // Fallback для ToastNotifier — если WinRT toast fails, пытаемся legacy balloon tip.
        // NotifyIcon должен быть Visible=true чтобы balloon tip сработал.
        Services.ToastNotifier.BalloonTipFallback = (title, body) =>
        {
            try
            {
                if (_notifyIcon is null) return;
                if (!_notifyIcon.Visible) _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(3000, title, body, System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { /* silent */ }
        };
    }

    /// <summary>Handle TaskbarCreated: Explorer restarted, tray icons are lost — re-create ours.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (WM_TASKBARCREATED != 0 && (uint)msg == WM_TASKBARCREATED)
        {
            // Explorer restarted — re-show tray icon if window is hidden (minimized to tray).
            if (_notifyIcon is not null && !IsVisible)
            {
                _notifyIcon.Visible = false; // reset
                _notifyIcon.Visible = true;  // re-register with new Explorer
            }
        }
        return IntPtr.Zero;
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            _notifyIcon!.Visible = false;
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    /// <summary>
    /// Nuclear exit path — обходит WPF Application.Shutdown + CRT exit полностью и
    /// вызывает Win32 TerminateProcess напрямую. Это единственный надёжный способ
    /// избежать deadlock'а в mrwebrtc.dll DllMain(DLL_PROCESS_DETACH) — см. три
    /// подряд dump'а в bug/logs-pc/windbg.txt от 2026-04-21 которые показали что
    /// ни Task.Delay watchdog, ни explicit Thread + P/Invoke не сработали (все
    /// три варианта зависели от того чтобы ExitProcess добежал до критической
    /// точки, но он зависал раньше).
    ///
    /// Что теряем и чем компенсировано:
    ///  - Application.Shutdown() / WPF cleanup — пропускается (Windows закроет все
    ///    window handles автоматически на TerminateProcess).
    ///  - VM.ShutdownAsync() — не вызывается. WS / peer / file transfer / audio
    ///    preflight cancel'ы пропускаются. Для WS это OK — сервер detect'ит
    ///    dead TCP и cleanup'ит. Active file transfers прервутся (OS-level, без
    ///    partial file corruption — файл открыт, TerminateProcess закроет handle).
    ///  - MainWindowRect не сохраняется в OnClosingAsync — компенсируется
    ///    debounced auto-save в OnRectChanged (LocationChanged / SizeChanged
    ///    events), rect актуален всегда после движения / ресайза.
    ///  - Telemetry heartbeat.Dispose() — noop.
    ///  - NotifyIcon — SafeDisposeNotifyIcon() делаем синхронно перед kill.
    /// </summary>
    private void ForceClose()
    {
        Dispatcher.Invoke(() =>
        {
            _forceClose = true;

            // 1. Sync save window rect (defense-in-depth, debounced save должен уже
            //    был отработать во время use, но на случай если окно закрывают без
            //    move/resize после debounce).
            try
            {
                var svc = new Services.SettingsService();
                var s = svc.Load();
                s.MainWindowRect = GetWindowRect(this);
                svc.Save(s);
            }
            catch { /* best effort */ }

            // 2. SYNC pipe signal сервису "user_exit" чтобы SessionMonitor не respawn'ил
            //    нового UI + persist'ил SuppressAutoSpawnUi=true в service-config.
            //
            //    Async версия + Task.Wait на UI thread DEADLOCK'ается: async continuation
            //    внутри pipe write хочет capture UI sync context, но UI thread заблокирован
            //    на Wait → timeout → bytes не flushed → service не получает сообщение →
            //    respawn UI через 800ms (подтверждено в bug/logs-pc/logs 2026-04-21).
            //
            //    Sync версия пишет bytes напрямую → kernel pipe buffer → service async
            //    ReadAsync подхватит даже если мы TerminateProcess через ms.
            try { Vm?.NotifyServiceUserExitSync(); } catch { /* best effort */ }

            // 3. Safe-dispose tray icon (WinForms requires STA thread — мы на UI thread).
            SafeDisposeNotifyIcon();

            // 4. Nuclear kill. TerminateProcess — kernel-level, не триггерит DllMain
            //    detach handlers → mrwebrtc не может deadlock'аться.
            try
            {
                TerminateProcess(GetCurrentProcess(), 0);
            }
            catch
            {
                try { Environment.Exit(0); } catch { /* ignore */ }
            }
        });
    }

    /// <summary>Safely dispose tray icon — detach context menu first to avoid WinForms race condition crash.</summary>
    private void SafeDisposeNotifyIcon()
    {
        try
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.ContextMenuStrip?.Dispose();
                _notifyIcon.ContextMenuStrip = null;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
        catch { /* ignore — WinForms race on shutdown */ }
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        // Save window position/size (debounced save в OnRectChanged должен уже был
        // отработать, но на всякий случай defense-in-depth sync save здесь тоже).
        try
        {
            var svc = new Services.SettingsService();
            var s = svc.Load();
            s.MainWindowRect = GetWindowRect(this);
            svc.Save(s);
        }
        catch { /* best effort */ }

        // Minimize to tray instead of closing (unless force close from tray menu, Меню→Выход, or setting disabled).
        // ВАЖНО: _closing НЕ ставим здесь — это не финальное закрытие, окно живёт в трее.
        // Если бы set'или, вторая X-кнопка после повторного открытия из трея уходила бы
        // в ранний `if (_closing) return;` без minimize + notification (bug от 2026-04-21).
        if (!_forceClose && Vm?.MinimizeToTray == true)
        {
            e.Cancel = true;

            // Показать tray icon + немедленно Hide окно. Уведомление даёт настоящий
            // Windows toast (ToastNotifier → WinRT Windows.UI.Notifications) в правом
            // нижнем углу + Action Center — legacy ShowBalloonTip часто подавляется
            // Win11 notification policy, а WinRT API respect'ит user settings корректно.
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = true;
            }
            Hide();
            Services.ToastNotifier.Show("ZConnect", Strings.Toast_MinimizedToTray);
            return;
        }

        // Real close (MinimizeToTray disabled OR _forceClose=true: X-кнопка при
        // MinimizeToTray=off, Меню→Выход, tray→Выход). Теперь — point of no return,
        // ставим _closing чтобы re-entrant события (например duplicate X-click во время
        // shutdown) не повторяли сценарий.
        _closing = true;
        e.Cancel = true; // cancel default close; nuclear kill не даст WPF dispose'ать
        ForceClose();
        await System.Threading.Tasks.Task.CompletedTask; // suppress unused async warning
    }

    /// <summary>Debounce таймер для auto-save MainWindowRect — вызывается из LocationChanged
    /// / SizeChanged events. Идея: пока user двигает/ресайзит окно, мы ставим таймер на
    /// 500ms; каждый новый event resets таймер; по истечении interval'а — save. Без
    /// debounce мы бы писали на диск каждый пиксель drag'а.</summary>
    private System.Windows.Threading.DispatcherTimer? _rectSaveTimer;

    /// <summary>Обработчик LocationChanged/SizeChanged на MainWindow — triggers debounced
    /// save. Позволяет nuclear ForceClose (kill без OnClosingAsync) не терять window
    /// position, т.к. rect сохраняется сразу после каждого move/resize.</summary>
    private void OnMainWindowRectChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;                              // игнорить events в init
        if (WindowState != WindowState.Normal) return;      // maximized/minimized — rect некорректен
        if (_rectSaveTimer is null)
        {
            _rectSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _rectSaveTimer.Tick += (_, _) =>
            {
                _rectSaveTimer!.Stop();
                try
                {
                    var svc = new Services.SettingsService();
                    var s = svc.Load();
                    s.MainWindowRect = GetWindowRect(this);
                    svc.Save(s);
                }
                catch { /* best effort */ }
            };
        }
        _rectSaveTimer.Stop();
        _rectSaveTimer.Start();
    }

    /// <summary>Запустить background thread который через timeoutMs мс убивает текущий процесс
    /// через Win32 TerminateProcess. Обходит deadlock в mrwebrtc.dll DllMain(DLL_PROCESS_DETACH)
    /// — kernel-level kill не вызывает DllMain detach handlers.
    ///
    /// DEPRECATED — оставлен как helper но не используется, nuclear ForceClose вызывает
    /// TerminateProcess синхронно без watchdog delay.</summary>
    private static void StartShutdownKillWatchdog(int timeoutMs)
    {
        var t = new System.Threading.Thread(() =>
        {
            try { System.Threading.Thread.Sleep(timeoutMs); }
            catch { /* interrupted — still try to kill */ }
            try
            {
                // Raw P/Invoke вместо Process.GetCurrentProcess().Kill() — минимум
                // managed code во время CLR shutdown, меньше шанс что кто-то абортит.
                TerminateProcess(GetCurrentProcessHandle(), 0);
            }
            catch
            {
                try { Environment.Exit(0); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "shutdown-kill-watchdog",
        };
        t.Start();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static IntPtr GetCurrentProcessHandle() => GetCurrentProcess();

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

    /// <summary>Закрыть окно удалённого экрана из CleanupConnectionAsync. AllowRealClose()
    /// обходит OnClosing handler (иначе он бы триггерил DisconnectViewerAsync в цикле).
    /// Nulling _remoteWindow гарантирует что следующий OpenOrActivateRemoteScreenWindow
    /// создаст свежий window со сброшенным bitmap.</summary>
    private void HideRemoteScreenWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_remoteWindow is not null)
            {
                try
                {
                    _remoteWindow.AllowRealClose();
                    _remoteWindow.Close();
                }
                catch { /* ignore */ }
                _remoteWindow = null;
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

            // WinRT Windows toast — native right-bottom notification + Action Center
            // (modern replacement для legacy _notifyIcon.ShowBalloonTip который
            // Win11 часто подавляет через Focus Assist / notification policy).
            Services.ToastNotifier.Show("ZConnect", Strings.Toast_ViewerConnectedToSession);

            // Показать tray icon временно если окно скрыто в трее. Через 5 сек
            // спрятать если окно видно (tray icon нужен только при minimize).
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = true;
                if (IsVisible)
                {
                    _viewerNotifyTimer?.Stop();
                    _viewerNotifyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    _viewerNotifyTimer.Tick += (_, _) => { _viewerNotifyTimer?.Stop(); if (IsVisible && _notifyIcon is not null) _notifyIcon.Visible = false; };
                    _viewerNotifyTimer.Start();
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
            Clipboard.SetText($"{Strings.Session_Copy_LoginLabel} {login}{Environment.NewLine}{Strings.Session_Copy_PasswordLabel} {pass}");
            ShowToast(Strings.Status_CopiedLoginPassword);
        }
        catch { /* ignore */ }
    }

    /// <summary>Returns true if any modal dialog owned by this window is currently shown.</summary>
    private bool HasOpenModalOwnedByUs()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w == this) continue;
            if (w.Owner == this && w.IsVisible) return true;
        }
        return false;
    }

    /// <summary>Animate scrim overlay opacity (0 or 0.32) with a short fade.</summary>
    private void ShowScrim(bool show)
    {
        if (Scrim is null) return;
        var anim = new DoubleAnimation
        {
            To = show ? 0.45 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        Scrim.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>
    /// Public helper для очистки scrim после закрытия owned dialog'а.
    /// Нужен когда после Close фокус не возвращается на MainWindow (Activated не fire'ится)
    /// — scrim остаётся "залипшим". Существующий workaround на line ~585 делает то же.
    /// Вызывается из других модальных окон (UnattendedPasswordDialog etc.).
    /// </summary>
    public void ForceScrimRefresh()
    {
        Dispatcher.BeginInvoke(() => ShowScrim(HasOpenModalOwnedByUs()));
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastOverlay.BeginAnimation(OpacityProperty, null);
        ToastTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ToastOverlay.Opacity = 0;
        ToastTransform.Y = 30;

        var easeOut = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var easeIn = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };

        // Snackbar bottom: slide up + fade in. After 2s, slide down + fade out.
        var sb = new Storyboard();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easeOut };
        Storyboard.SetTarget(fadeIn, ToastOverlay);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        sb.Children.Add(fadeIn);

        var slideIn = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = easeOut };
        Storyboard.SetTarget(slideIn, ToastTransform);
        Storyboard.SetTargetProperty(slideIn, new PropertyPath(TranslateTransform.YProperty));
        sb.Children.Add(slideIn);

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
        {
            BeginTime = TimeSpan.FromSeconds(2),
            EasingFunction = easeIn
        };
        Storyboard.SetTarget(fadeOut, ToastOverlay);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
        sb.Children.Add(fadeOut);

        var slideOut = new DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(320))
        {
            BeginTime = TimeSpan.FromSeconds(2),
            EasingFunction = easeIn
        };
        Storyboard.SetTarget(slideOut, ToastTransform);
        Storyboard.SetTargetProperty(slideOut, new PropertyPath(TranslateTransform.YProperty));
        sb.Children.Add(slideOut);

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

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = b;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        var w = new Window
        {
            Style = null,
            Title = Strings.Help_Title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = this,
            Background = (System.Windows.Media.Brush?)Application.Current?.Resources["SurfaceBgBrush"]
                        ?? System.Windows.Media.Brushes.White,
        };
        ApplyDarkTitleBar(w, _isDarkTheme);
        w.PreviewKeyDown += (_, ev) => { if (ev.Key == Key.Escape) { w.Close(); ev.Handled = true; } };

        var primary = (System.Windows.Media.Brush?)Application.Current?.Resources["TextPrimaryBrush"]
                      ?? System.Windows.Media.Brushes.Black;
        var secondary = (System.Windows.Media.Brush?)Application.Current?.Resources["TextSecondaryBrush"]
                        ?? System.Windows.Media.Brushes.Gray;
        var kbdBg = (System.Windows.Media.Brush?)Application.Current?.Resources["SurfaceAltBgBrush"]
                    ?? System.Windows.Media.Brushes.LightGray;
        var kbdBorder = (System.Windows.Media.Brush?)Application.Current?.Resources["BorderBrush"]
                        ?? System.Windows.Media.Brushes.DarkGray;

        var stack = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };

        void AddShortcut(string keys, string desc)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var keyBorder = new Border
            {
                Background = kbdBg,
                BorderBrush = kbdBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = keys,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = primary
                }
            };
            Grid.SetColumn(keyBorder, 0);
            row.Children.Add(keyBorder);

            var descBlock = new TextBlock
            {
                Text = desc,
                FontSize = 12,
                Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(descBlock, 1);
            row.Children.Add(descBlock);

            stack.Children.Add(row);
        }

        stack.Children.Add(new TextBlock
        {
            Text = Strings.Help_Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = primary,
            Margin = new Thickness(0, 0, 0, 10)
        });

        AddShortcut("Ctrl + T", Strings.Help_Shortcut_ToggleTheme);
        AddShortcut("Ctrl + ,", Strings.Help_Shortcut_OpenSettings);
        AddShortcut("Ctrl + 1 / 2 / 3", Strings.Help_Shortcut_MainTabs);
        AddShortcut("Esc", Strings.Help_Shortcut_CloseDialog);

        w.Content = stack;
        w.ShowDialog();
        // Явная очистка scrim: событийный путь через Deactivated/Activated
        // иногда не срабатывает (фокус уходит не на MainWindow после close)
        // и затемнение остаётся висеть.
        ShowScrim(false);
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
                // Set dark title bar BEFORE Show so DWM renders correctly from the start
                ApplyDarkTitleBar(_systemSettingsWindow, _isDarkTheme);
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

    /// <summary>Клик по красному lockout-баннеру в MainWindow — открывает Настройки
    /// (вкладка «Общие» показана по умолчанию) чтобы пользователь мог нажать
    /// «Снять блокировку» или увидеть детали состояния.</summary>
    private void LockoutBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MenuSystem_Click(sender, new RoutedEventArgs());
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        ForceClose();
    }

    private void MenuHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, Strings.Help_NotYetMessage, Strings.Help_AboutTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Match LogService: C:\ProgramData\ZConect\logs\ui.log
            var logsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ZConect", "logs", "ui.log");
            if (!File.Exists(logsPath))
            {
                MessageBox.Show(this, Strings.Logs_NotFound, Strings.Logs_Category, MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, string.Format(Strings.Logs_OpenError_Format, ex.Message), Strings.Logs_Category, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool _isDarkTheme;
    private ResourceDictionary? _themeOverride;

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme(_isDarkTheme);

        // Persist theme choice. Audit fix 2026-04-26: log exception если IO/permission
        // fail — раньше silently swallow'ил → user видел themed UI, restart возвращал
        // старую тему без объяснения почему.
        try
        {
            var svc = new Services.SettingsService();
            var s = svc.Load();
            s.ThemeMode = _isDarkTheme ? "Dark" : "Light";
            svc.Save(s);
        }
        catch (Exception ex)
        {
            // Использовать новый LogService — он file-based, не conflict.
            try { new Services.LogService().Warn("Settings", $"theme_toggle_save_failed: {ex.Message}"); }
            catch { /* logging itself failed */ }
        }
    }

    /// <summary>Подменяет Wpf.Ui ThemesDictionary в Application.Resources на Light/Dark.
    /// Идентично тому что делает ApplicationThemeManager.Apply (в части стилей
    /// TextBox/ComboBox/Button/TabControl), но БЕЗ вызова WindowBackgroundManager —
    /// последний ломает DWM caption rendering на Win11 24H2.</summary>
    private static void SwapWpfUiThemesDictionary(bool dark)
    {
        var appDicts = Application.Current.Resources.MergedDictionaries;
        // Удаляем существующий ThemesDictionary (marked by class type — stable API).
        for (int i = appDicts.Count - 1; i >= 0; i--)
        {
            if (appDicts[i] is Wpf.Ui.Markup.ThemesDictionary)
                appDicts.RemoveAt(i);
        }
        // Вставляем новый ThemesDictionary в начало (порядок важен: base styles → overrides).
        var themesDict = new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                         : Wpf.Ui.Appearance.ApplicationTheme.Light
        };
        appDicts.Insert(0, themesDict);
    }

    private void ApplyTheme(bool dark)
    {
        // 1. WPF-UI theme FIRST — sets base styles for TextBox, ComboBox, TabControl.
        //    Must happen BEFORE we parse AppStyles.xaml, because AppStyles uses
        //    BasedOn="{StaticResource {x:Type TextBox}}" which captures the WPF-UI base.
        //
        // NOTE (2026-04-21): НЕ используем ApplicationThemeManager.Apply() — он
        // internally зовёт WindowBackgroundManager.UpdateBackground() для всех открытых
        // Window, который применяет Mica/Acrylic backdrop манипуляции. На Win11 24H2
        // после этого DWM перестаёт рендерить caption text ("ZConnect" был невидим).
        //
        // Вместо этого меняем Wpf.Ui ThemesDictionary напрямую в App.Resources —
        // это даёт нам тёмные стили TextBox/ComboBox/Button/TabControl БЕЗ вызова
        // WindowBackgroundManager (который ломает title bar).
        SwapWpfUiThemesDictionary(dark);

        // 2. Ensure our wrapper dictionary exists (added once, never removed).
        var dicts = Application.Current.Resources.MergedDictionaries;
        if (_themeOverride == null)
        {
            _themeOverride = new ResourceDictionary();
            dicts.Add(_themeOverride);
        }

        // 3. Swap the wrapper's CONTENT — colors + styles for the target theme.
        //    AppStyles is re-parsed NOW (after Apply set the correct WPF-UI base),
        //    so BasedOn and StaticResource in trigger setters resolve correctly.
        _themeOverride.MergedDictionaries.Clear();
        _themeOverride.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                dark ? "Themes/DarkColors.xaml" : "Themes/LightColors.xaml",
                UriKind.Relative)
        });
        _themeOverride.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/AppStyles.xaml", UriKind.Relative)
        });

        // 4. Apply dark title bar to main window (DWM per-window — matches dialogs).
        ApplyDarkTitleBar(this, dark);

        // 5. Update toggle button icon with fade animation
        ThemeIcon.Text = dark ? "\uE706" : "\uE708";
        ThemeToggleButton.ToolTip = dark ? Strings.Theme_Light_Tooltip : Strings.Theme_Dark_Tooltip;
        var fadeIn = new DoubleAnimation
        {
            From = 0.2,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        ThemeIcon.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    /// <summary>Enter in Pass field triggers Join when both codes valid.</summary>
    private void JoinPassBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (vm.JoinLoginCode?.Length == 8 && vm.JoinPassCode?.Length == 8
            && vm.JoinLoginCode.All(char.IsDigit) && vm.JoinPassCode.All(char.IsDigit)
            && vm.JoinSessionCommand.CanExecute(null))
        {
            vm.JoinSessionCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.T:
                ThemeToggle_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.OemComma: // Ctrl+,
                MenuSystem_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.D1: case Key.NumPad1:
                if (MainTabs.Items.Count > 0) { MainTabs.SelectedIndex = 0; e.Handled = true; }
                break;
            case Key.D2: case Key.NumPad2:
                if (MainTabs.Items.Count > 1) { MainTabs.SelectedIndex = 1; e.Handled = true; }
                break;
            case Key.D3: case Key.NumPad3:
                if (MainTabs.Items.Count > 2) { MainTabs.SelectedIndex = 2; e.Handled = true; }
                break;
        }
    }

    // ── Digits-only input handlers for JoinLoginBox / JoinPassBox ─────

    /// <summary>Rejects any non-digit character from keyboard input.</summary>
    private void DigitsOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (!e.Text.All(char.IsDigit))
            e.Handled = true;
    }

    /// <summary>Strips non-digit characters from pasted content (or cancels paste).
    /// Smart-paste: if clipboard has exactly two 8-digit tokens separated by whitespace,
    /// fill login + password fields at once regardless of which field is focused.</summary>
    private void DigitsOnly_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true))
        {
            e.CancelCommand();
            return;
        }
        var text = (string)e.SourceDataObject.GetData(DataFormats.UnicodeText, true);

        // Smart paste: "12345678 87654321" (or with newlines/tabs) → split into both fields.
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 2
            && tokens[0].Length == 8 && tokens[0].All(char.IsDigit)
            && tokens[1].Length == 8 && tokens[1].All(char.IsDigit)
            && DataContext is ViewModels.MainViewModel vm)
        {
            vm.JoinLoginCode = tokens[0];
            vm.JoinPassCode = tokens[1];
            e.CancelCommand();
            return;
        }

        if (!text.All(char.IsDigit))
            e.CancelCommand();
    }

    // ── Per-window DWM dark title bar ─────────────────────────────

    /// <summary>
    /// Force explicit title bar text color via DWMWA_TEXT_COLOR (attr 36) and
    /// caption background via DWMWA_CAPTION_COLOR (attr 35). Windows 11 22H2+.
    /// Required because with Mica backdrop, the default title text color may
    /// match the backdrop, making it invisible.
    /// </summary>
    internal static void ForceTitleBarTextColor(Window w, bool dark)
    {
        void Apply()
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                // COLORREF format: 0x00BBGGRR
                int textColor = dark
                    ? 0x00E8ECF4 /* light text */
                    : 0x001F2E3D /* dark text */;
                DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref textColor, sizeof(int));
            }
            catch { /* older Windows — ignore */ }
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>Set Title via WPF + Win32 SetWindowText (defense-in-depth).
    /// WPF Title — стандартный path; SetWindowText — native fallback если theme
    /// styles перехватывают Title property.</summary>
    private void ApplyWindowTitle(string title)
    {
        try
        {
            Title = title;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SetWindowText(hwnd, title);
        }
        catch { /* ignore */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    internal static void ApplyDarkTitleBar(Window w, bool dark)
    {
        // Don't call EnsureHandle — for programmatic `new Window {}` dialogs it triggers
        // early style resolution that tries to coerce AllowsTransparency AFTER the HWND
        // exists, causing InvalidOperationException and preventing the dialog from showing.
        // Rely on SourceInitialized event which fires right after HWND creation during Show.
        void Apply()
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int value = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
                // Disable Mica/Acrylic backdrop (DWMSBT_NONE=1) so Windows title bar
                // renders opaque with Title text visible.
                int backdrop = 1;
                DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref backdrop, sizeof(int));
                // Force title bar text color to contrast the backdrop. Без этого
                // на Win11 22H2+ когда backdrop=None (no Mica), Windows auto-picks
                // цвет text которое может совпасть с фоном → title невидим.
                // COLORREF 0x00BBGGRR: light text для dark, dark text для light.
                int textColor = dark ? 0x00E8ECF4 : 0x001F2E3D;
                DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref textColor, sizeof(int));
            }
            catch { /* ignore on older Windows */ }
        }
        w.SourceInitialized += (_, _) => Apply();
        // Also apply if window is already initialized (e.g. called from ApplyTheme
        // while MainWindow is already shown).
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
    }

    // ── Window position/size persistence ────────────────────────────

    internal static void RestoreWindowRect(Window w, Models.WindowRect r)
    {
        if (r.Width <= 0 || r.Height <= 0)
            return;

        // Ensure the saved position is on a visible monitor
        var rect = new System.Drawing.Rectangle(
            (int)r.Left, (int)r.Top, (int)r.Width, (int)r.Height);
        bool onScreen = false;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(rect))
            {
                onScreen = true;
                break;
            }
        }
        if (!onScreen) return;

        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = r.Left;
        w.Top = r.Top;
        w.Width = r.Width;
        w.Height = r.Height;
        if (r.IsMaximized)
            w.WindowState = WindowState.Maximized;
    }

    internal static Models.WindowRect GetWindowRect(Window w)
    {
        var r = new Models.WindowRect();
        if (w.WindowState == WindowState.Maximized)
        {
            r.IsMaximized = true;
            var rb = w.RestoreBounds;
            r.Left = rb.Left;
            r.Top = rb.Top;
            r.Width = rb.Width;
            r.Height = rb.Height;
        }
        else
        {
            r.Left = w.Left;
            r.Top = w.Top;
            r.Width = w.ActualWidth;
            r.Height = w.ActualHeight;
        }
        return r;
    }

    /// <summary>Клик по «В адресную книгу →» в Recent row — переключает главный TabControl
    /// на Address Book (третья вкладка, index=2).</summary>
    private void RecentSeeAll_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabs is not null && MainTabs.Items.Count > 2)
        {
            MainTabs.SelectedIndex = 2;
        }
    }

    /// <summary>Menu → «Очистить «Недавно подключённые»». Удаляет ephemeral контакты
    /// (IsAutoGenerated=true, имена вида «Сеанс XXXX») полностью, а у named контактов
    /// сбрасывает LastConnectedUtc → null — они остаются в Address Book, но исчезают
    /// из Recent row. Разделение логично: ephemeral не несут ценности без recent-контекста,
    /// named — value asset пользователя, удалять без спроса неправильно.</summary>
    private void MenuClearRecent_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (!Dialogs.AppDialog.Confirm(
                Strings.Menu_ClearRecent_Confirm,
                confirmText: Strings.Button_Clear))
            return;
        Vm.ClearRecentContacts();
    }

    private void TabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is not System.Windows.Controls.TabControl tc) return;

        var presenter = tc.Template?.FindName("PART_Content", tc) as UIElement;
        if (presenter is not null)
        {
            var fade = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = TimeSpan.FromMilliseconds(150)
            };
            presenter.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // Auto-focus login field when "Подключиться" tab (index 1) becomes active.
        if (tc.SelectedIndex == 1 && JoinLoginBox is not null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                JoinLoginBox.Focus();
                Keyboard.Focus(JoinLoginBox);
            }), System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    /// <summary>Scroll вновь выбранного контакта into view — для случая когда
    /// SelectedContact меняется из VM (например после AddContact).</summary>
    private void ContactsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem is not null)
        {
            lb.ScrollIntoView(lb.SelectedItem);
        }
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {

    }

    /// <summary>Click на update badge → открывает DownloadUrl в default браузере.
    /// Никакого auto-download / auto-install — user сам качает и ставит installer.
    ///
    /// Audit fix 2026-04-24 (H2): validate URL scheme + host ДО ShellExecute.
    /// Без проверки compromised signaling мог бы set DownloadUrl='file://attacker/malware.exe'
    /// или javascript: URI → ShellExecute запустил бы в доверенном контексте.
    /// Whitelist только http/https (обычно https для prod); reject всё остальное.</summary>
    private void UpdateBadge_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(vm.UpdateDownloadUrl))
            return;
        // Validate URL: только http/https. file://, javascript:, UNC-paths — reject.
        if (!Uri.TryCreate(vm.UpdateDownloadUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            // Refuse to open suspicious URL. Show user-facing message rather than silent fail.
            MessageBox.Show(this,
                $"Invalid update URL scheme: {vm.UpdateDownloadUrl}\n\nOnly http:// and https:// allowed. Check with administrator if this persists.",
                "Update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        try
        {
            // Use the validated `uri.AbsoluteUri` вместо raw string чтобы избежать
            // shell expansion на каких-то экзотических форматах.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch { /* invalid url / no browser — silently ignore */ }
    }

    // ── Onboarding (first-launch 3-step guide) ──────────────────────

    private int _onboardingStep = 1;

    // Onboarding steps — titles/texts pulled lazy через OnboardingSteps (не static,
    // чтобы resource lookup шёл при текущей culture, а не at type-init).
    private static (string Title, string Text, string IconData)[] OnboardingSteps => new[]
    {
        // Step 1 — welcome
        (Strings.Onboarding_Welcome_Title,
         Strings.Onboarding_Step1_Text_Full,
         "M8,1 V7 H14 V9 H8 V15 H6 V9 H0 V7 H6 V1 Z"),
        // Step 2 — create session
        (Strings.Onboarding_Step2_Title,
         Strings.Onboarding_Step2_Text,
         "M2,2 H14 V11 H9 L7,13 L5,11 H2 Z"),
        // Step 3 — connect to someone
        (Strings.Onboarding_Step3_Title,
         Strings.Onboarding_Step3_Text,
         "M5.5,1 C3.6,1 2,2.6 2,4.5 C2,6.4 3.6,8 5.5,8 C7.4,8 9,6.4 9,4.5 C9,2.6 7.4,1 5.5,1 Z M0,15 C0,12 2.5,10 5.5,10 C8.5,10 11,12 11,15 Z"),
        // Step 4 — privacy/telemetry disclosure (152-ФЗ soft consent — checkbox not required).
        (Strings.Onboarding_Step4_Title,
         Strings.Onboarding_Step4_Text,
         "M8,1 L2,3.5 V8 C2,11.5 4.5,14.5 8,15 C11.5,14.5 14,11.5 14,8 V3.5 Z M7,11 L4,8 L5.4,6.6 L7,8.2 L10.6,4.6 L12,6 Z"),
    };

    private void ShowOnboarding()
    {
        _onboardingStep = 1;
        RenderOnboardingStep();
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void RenderOnboardingStep()
    {
        var idx = _onboardingStep - 1;
        if (idx < 0 || idx >= OnboardingSteps.Length) return;
        var (title, text, iconData) = OnboardingSteps[idx];
        OnboardingStepTitle.Text = title;
        OnboardingStepText.Text = text;
        OnboardingStepIcon.Data = System.Windows.Media.Geometry.Parse(iconData);
        // Dots: fill current step in primary, rest in border brush.
        var primary = (System.Windows.Media.Brush?)Application.Current?.Resources["PrimaryBrush"]
                      ?? System.Windows.Media.Brushes.DodgerBlue;
        var border = (System.Windows.Media.Brush?)Application.Current?.Resources["BorderBrush"]
                     ?? System.Windows.Media.Brushes.LightGray;
        OnboardingDot1.Fill = _onboardingStep >= 1 ? primary : border;
        OnboardingDot2.Fill = _onboardingStep >= 2 ? primary : border;
        OnboardingDot3.Fill = _onboardingStep >= 3 ? primary : border;
        OnboardingDot4.Fill = _onboardingStep >= 4 ? primary : border;
        OnboardingNextButton.Content = _onboardingStep >= OnboardingSteps.Length ? Strings.Button_Done : Strings.Button_Next;
        OnboardingSkipButton.Visibility = _onboardingStep >= OnboardingSteps.Length
            ? Visibility.Collapsed
            : Visibility.Visible;
        // Privacy panel (link + checkbox) виден только на 4-м шаге. Чекбокс — soft:
        // не блокирует «Готово», но если поставлен — пишется PrivacyPolicyAckedAtUtc
        // в settings (audit trail для 152-ФЗ).
        OnboardingPrivacyPanel.Visibility = _onboardingStep == 4
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnboardingNext_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep < OnboardingSteps.Length)
        {
            _onboardingStep++;
            RenderOnboardingStep();
            return;
        }
        CompleteOnboarding();
    }

    private void OnboardingSkip_Click(object sender, RoutedEventArgs e)
    {
        CompleteOnboarding();
    }

    private void CompleteOnboarding()
    {
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        // Снимаем состояние privacy-чекбокса с 4-го шага (если user туда дошёл).
        // Если пропустил wizard через "Skip" до 4-го шага — privacyAcked=false,
        // никакого timestamp'а не пишем (но onboarding всё равно помечается завершённым).
        var privacyAcked = OnboardingPrivacyCheckbox?.IsChecked == true;
        // Bug fix 2026-04-26: раньше делали standalone Load/modify/Save — но
        // ViewModel'я _settings оставался с OnboardingCompleted=false в памяти.
        // Любой следующий Vm.SaveSettings() (close Settings / change language /
        // video preset) перезаписывал true → false → onboarding запускался снова
        // после каждого выхода. Fix: обновляем in-memory _settings через VM
        // вместо обхода через standalone svc.Save().
        if (Vm is not null)
        {
            try { Vm.MarkOnboardingCompleted(privacyAcked); }
            catch { /* ignore — next launch will show again, not critical */ }
        }
        else
        {
            // Fallback на случай если DataContext ещё не инициализирован.
            try
            {
                var svc = new Services.SettingsService();
                var s = svc.Load();
                s.OnboardingCompleted = true;
                if (privacyAcked && s.PrivacyPolicyAckedAtUtc is null)
                {
                    s.PrivacyPolicyAckedAtUtc = DateTime.UtcNow;
                }
                svc.Save(s);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Открывает Политику конфиденциальности в дефолтном браузере.
    /// Hyperlink в onboarding-overlay (не дублируем общий Hyperlink_RequestNavigate
    /// из SystemSettingsWindow — там другой scope). Используем ShellExecute через
    /// ProcessStartInfo с UseShellExecute=true.
    /// </summary>
    private void Onboarding_Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.ToString(),
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch
        {
            // Если браузер не открылся — silent ignore, user может скопировать ссылку
            // вручную из политики приложения (или скриншотом).
        }
    }
}

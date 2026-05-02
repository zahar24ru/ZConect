using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UiApp;

public partial class App : Application
{
    // Local\\ namespace — per-session kernel object. Works under normal user token
    // without requiring SeCreateGlobalPrivilege. Both the service-spawned UI
    // (via CreateProcessAsUser into user session 1) and the shortcut-launched UI
    // run in the same session → both see the same Local\\ mutex.
    private const string MutexName = "Local\\ZConect_SingleInstance_F47AC10B";
    private const string EventName = "Local\\ZConect_ShowWindow_F47AC10B";
    private Mutex? _mutex;
    private bool _mutexOwned;
    private EventWaitHandle? _showEvent;
    private Thread? _showEventThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply UI culture from ClientSettings (default = system). Должно быть ПЕРЕД
        // любым UI init (MainWindow etc.) иначе Strings.resx lookup'ы используют старую.
        try
        {
            var settingsService = new Services.SettingsService();
            var settings = settingsService.Load();

            // One-time seed from installer-picked language. Installer пишет
            // C:\ProgramData\ZConect\initial-language.txt с "ru-RU" или "en-US"
            // в зависимости от того что юзер выбрал в wizard'е. На первом запуске
            // (когда settings.UiLanguage ещё "") — адоптируем эту культуру и
            // персистим в ClientSettings, чтобы user не видел русский UI если
            // выбрал English в installer'е. Файл НЕ удаляем: на той же машине
            // новые user accounts тоже должны получить тот же дефолт до явного
            // изменения в Настройках.
            if (string.IsNullOrEmpty(settings.UiLanguage))
            {
                try
                {
                    var seedPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "ZConect", "initial-language.txt");
                    if (File.Exists(seedPath))
                    {
                        var code = File.ReadAllText(seedPath).Trim();
                        if (code == "en-US" || code == "ru-RU")
                        {
                            settings.UiLanguage = code;
                            settingsService.Save(settings);
                        }
                    }
                }
                catch { /* seed is optional — ignore errors, fall back to system default */ }
            }

            if (!string.IsNullOrEmpty(settings.UiLanguage))
            {
                var ci = new System.Globalization.CultureInfo(settings.UiLanguage);
                System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ci;
                System.Threading.Thread.CurrentThread.CurrentUICulture = ci;
                UiApp.Properties.Strings.Culture = ci;
            }
        }
        catch { /* invalid locale — stick with system default */ }

        // Регистрация AppUserModelID + HKCU registry entry для Windows toast notifications.
        // CreateToastNotifier требует оба — иначе падает с COMException. Register logging
        // пишется в shared LogService (ui.log) чтобы видеть при debug что toast
        // registered корректно и работает.
        var logSvc = new Services.LogService();
        Services.ToastNotifier.Register("ZConnect", msg => logSvc.Debug("Toast", msg));

        // Faster tooltips (default ~1s is sluggish).
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(400));

        // Single-instance enforcement — applies to BOTH shortcut-launched and
        // service-spawned instances. The service launches the UI via
        // CreateProcessAsUser with the interactive user's token, so Global\\ mutex
        // and event are created under normal user permissions and accessible to
        // any subsequent shortcut launch by the same user.
        //
        // Flow:
        //  1. Service spawns first instance (--from-service) → creates mutex + event.
        //  2. User double-clicks shortcut → second instance sees mutex held → opens
        //     the named event → Sets it → existing UI restores its window → second
        //     process exits.

        // Wrap the whole single-instance block — if mutex creation fails for any reason
        // (permission denied across session boundary, etc.) we fall through to starting
        // as a new instance. Better to have two UIs than a hard crash on startup.
        bool signaledExisting = false;
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            _mutexOwned = createdNew;

            if (!createdNew)
            {
                // Another instance holds the mutex — signal it to bring its window to the front.
                try
                {
                    var evt = EventWaitHandle.OpenExisting(EventName);
                    evt.Set();
                    evt.Dispose();
                    signaledExisting = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SingleInstance] could not signal existing instance: {ex.Message}");
                }

                try { _mutex?.Dispose(); } catch { }
                _mutex = null;
                _mutexOwned = false;
            }
        }
        catch (Exception ex)
        {
            // UnauthorizedAccessException across sessions, or other rare failures.
            // Fall through to normal startup as a fresh instance.
            System.Diagnostics.Debug.WriteLine($"[SingleInstance] mutex setup failed: {ex.GetType().Name}: {ex.Message}");
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
            _mutexOwned = false;
        }

        if (signaledExisting)
        {
            Shutdown();
            return;
        }

        // First instance (or mutex failure) — try to listen for show-window signals.
        if (_mutexOwned)
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                _showEventThread = new Thread(WaitForShowSignal) { IsBackground = true, Name = "SingleInstanceListener" };
                _showEventThread.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SingleInstance] event setup failed: {ex.Message}");
                try { _showEvent?.Dispose(); } catch { }
                _showEvent = null;
            }
        }

        base.OnStartup(e);

        // UF-01 fix: log unhandled exceptions to file (crashes.log) — not just Debug.WriteLine,
        // which gets lost when app runs without a debugger attached. This helps diagnose
        // silent crashes that occur in production (e.g. from background Tasks/threads).

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        // Non-UI thread crashes (ThreadPool, background threads) — not recoverable but logged.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog($"AppDomain.UnhandledException terminating={args.IsTerminating}",
                args.ExceptionObject as Exception);
        };

        // mrwebrtc 2.0.2 pre-flight: default audio endpoint at unsupported sample rate
        // (e.g. 192 kHz studio interface) crashes the native lib with c0000409 FAST_FAIL
        // (FAST_FAIL_FATAL_APP_EXIT, subcode 7) at PeerConnection init — kernel-level
        // abort, unrecoverable via managed try/catch. Hard-exit если rate вне whitelist —
        // раньше был только MessageBox OK → user нажимал OK → app продолжал → crash dump
        // (observed 2026-04-19).
        try
        {
            var audio = Services.AudioPreflight.Check();
            if (!audio.Ok)
            {
                var list = string.Join("\n", audio.Problematic.Select(p =>
                {
                    var idTail = p.Id.Contains('.') ? p.Id[(p.Id.LastIndexOf('.') + 1)..] : p.Id;
                    // Показываем friendly name + id в скобках. Если friendly name не удалось
                    // прочитать (ShortenDeviceId fallback совпадает с idTail) — выводим только id.
                    return p.FriendlyName.Equals(idTail, StringComparison.OrdinalIgnoreCase)
                        ? $"  • {idTail} — {p.SampleRate} Гц"
                        : $"  • {p.FriendlyName} {idTail} — {p.SampleRate} Гц";
                }));
                MessageBox.Show(
                    "Обнаружены аудиоустройства Windows с частотой дискретизации, " +
                    "которая не поддерживается встроенной библиотекой WebRTC " +
                    "(поддерживаются только 8, 16, 24, 32, 44.1, 48, 96 кГц):\n\n" +
                    list + "\n\n" +
                    "Запуск приложения приведёт к аварийному выходу (FAST_FAIL в mrwebrtc.dll).\n\n" +
                    "Как исправить:\n" +
                    "1. Откройте Параметры → Система → Звук\n" +
                    "2. Найдите перечисленные выше устройства, откройте Свойства → Дополнительно\n" +
                    "3. Установите Формат по умолчанию = 48000 Гц (16 или 24 бит)\n" +
                    "4. Либо отключите устройство, если оно не нужно\n" +
                    "5. Запустите приложение повторно\n\n" +
                    "Приложение будет закрыто.",
                    "Несовместимая частота аудио — запуск заблокирован",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }
        catch { /* pre-flight best-effort; fail-open */ }
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            // C:\ProgramData\ZConect\logs\crashes.log — shared location, writable by
            // SYSTEM (service-spawned UI) and regular users.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ZConect", "logs");
            try { Directory.CreateDirectory(dir); } catch { }
            var path = Path.Combine(dir, "crashes.log");
            var ts = DateTime.UtcNow.ToString("o");
            var text = $"[{ts}] {source}\n{ex}\n----\n";
            File.AppendAllText(path, text);
            System.Diagnostics.Debug.WriteLine($"[{source}] {ex}");
        }
        catch { /* never crash the crash handler */ }
    }

    private void WaitForShowSignal()
    {
        while (_showEvent is not null)
        {
            try
            {
                if (!_showEvent.WaitOne(Timeout.Infinite))
                    continue;

                // Another instance signaled us — fully restore and foreground the main window.
                Dispatcher.Invoke(() =>
                {
                    var win = MainWindow;
                    if (win is null) return;
                    // Restore full visibility — may have been Hide()/ShowInTaskbar=false by tray logic.
                    win.Show();
                    win.ShowInTaskbar = true;
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    // Topmost trick to bypass Windows foreground restrictions, then release.
                    var wasTopmost = win.Topmost;
                    win.Topmost = true;
                    win.Activate();
                    win.Topmost = wasTopmost;
                    win.Focus();
                });
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // ignore
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Safety net: ensure background resources are released even if window close handlers were skipped.
        try
        {
            if (Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
            {
                vm.Shutdown();
            }
        }
        catch
        {
            // ignore
        }

        try { _showEvent?.Dispose(); _showEvent = null; } catch { }
        if (_mutexOwned)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* not owned — second instance path */ }
        }
        _mutex?.Dispose();

        base.OnExit(e);
    }

}

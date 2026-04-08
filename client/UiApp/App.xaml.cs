using System.Windows;
using System.Windows.Threading;

namespace UiApp;

public partial class App : Application
{
    private const string MutexName = "Global\\ZConect_SingleInstance_F47AC10B";
    private const string EventName = "Global\\ZConect_ShowWindow_F47AC10B";
    private Mutex? _mutex;
    private bool _mutexOwned;
    private EventWaitHandle? _showEvent;
    private Thread? _showEventThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        _mutexOwned = createdNew;

        if (!createdNew)
        {
            // Another instance is running — signal it to show its window.
            try
            {
                var evt = EventWaitHandle.OpenExisting(EventName);
                evt.Set();
                evt.Dispose();
            }
            catch { /* ignore — first instance will handle it */ }

            Shutdown();
            return;
        }

        // Create a named event that other instances can signal to restore our window.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _showEventThread = new Thread(WaitForShowSignal) { IsBackground = true, Name = "SingleInstanceListener" };
        _showEventThread.Start();

        base.OnStartup(e);

        // Prevent unobserved task exceptions (fire-and-forget async) from crashing the process.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };

        // Catch WPF dispatcher unhandled exceptions.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
        };
    }

    private void WaitForShowSignal()
    {
        while (_showEvent is not null)
        {
            try
            {
                if (!_showEvent.WaitOne(Timeout.Infinite))
                    continue;

                // Another instance signaled us — restore and activate main window.
                Dispatcher.Invoke(() =>
                {
                    var win = MainWindow;
                    if (win is null) return;
                    win.Show();
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    win.Activate();
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

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ZConectService;

/// <summary>
/// Service-side input injection agent. UI (под user token после Stage 1)
/// больше не может инжектить на secure desktop — делегирует сюда через pipe.
/// Thread-dedicated: держит текущий active input desktop (Default или
/// Winlogon) через OpenInputDesktop+SetThreadDesktop, обновляет его каждый
/// цикл обработки чтобы следовать за переключением (UAC prompt).
/// </summary>
internal sealed class InputInjectionAgent : IDisposable
{
    private readonly ServiceLogger _log;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _worker;
    private readonly CancellationTokenSource _cts = new();
    private IntPtr _currentDesktop = IntPtr.Zero;

    public InputInjectionAgent(ServiceLogger log)
    {
        _log = log;

        // Service по-умолчанию живёт в winstation "Service-0x0-3e7$" (session 0).
        // SetThreadDesktop для WinSta0\Default требует чтобы process был
        // в WinSta0. Без этого switch'а injection silent-fails.
        try
        {
            var winsta0 = OpenWindowStation("WinSta0", false, WINSTA_ALL_ACCESS);
            if (winsta0 != IntPtr.Zero)
            {
                if (SetProcessWindowStation(winsta0))
                    _log.Info("InputAgent", "process_winstation_switched_to_winsta0");
                else
                    _log.Warn("InputAgent", $"set_process_winstation_failed err={Marshal.GetLastWin32Error()}");
            }
            else
            {
                _log.Warn("InputAgent", $"open_window_station_failed err={Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex) { _log.Warn("InputAgent", $"winstation_switch_exception: {ex.Message}"); }

        _worker = new Thread(WorkerLoop)
        {
            Name = "ZConect-InputInjection",
            IsBackground = true
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
        _log.Info("InputAgent", "worker_thread_started");
    }

    public void EnqueueMouse(string action, int x, int y, int button, int delta)
    {
        try { _queue.Add(() => DoInjectMouse(action, x, y, button, delta)); }
        catch (InvalidOperationException) { /* disposed */ }
    }

    public void EnqueueKeyboard(string action, int vk, int scanCode)
    {
        try { _queue.Add(() => DoInjectKeyboard(action, vk, scanCode)); }
        catch (InvalidOperationException) { }
    }

    private void WorkerLoop()
    {
        // ВАЖНО (2026-04-18): OperationCanceledException из
        // GetConsumingEnumerable при cancel'е бросается НА `foreach` MoveNext
        // (т.е. на итерации enumerator'а), ДО входа в тело loop'а. `catch`
        // внутри body не может его поймать — exception propagates out of
        // WorkerLoop unhandled → thread terminates with unhandled exception
        // → Windows Error Reporting создаёт dump в каждый service stop.
        // Ловим на уровне метода + break из loop на cancellation.
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    // Impersonate winlogon.exe token active user session — для
                    // injection на user's WinSta0\Winlogon (secure desktop). Без
                    // impersonation thread видит только service's session 0.
                    // RAII Scope автоматически RevertToSelf при выходе.
                    using var imp = WinlogonImpersonation.Enter(_log);
                    EnsureCurrentInputDesktop();
                    action();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.Warn("InputAgent", $"inject_exception: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation при service stop — GetConsumingEnumerable
            // бросает OCE когда _cts signaled. Ожидаемый graceful shutdown.
            _log.Debug("InputAgent", "worker_loop_cancelled");
        }
        catch (Exception ex)
        {
            // Любое другое unhandled — логируем чтобы не создавать WER dump silently.
            _log.Error("InputAgent", "worker_loop_fatal", ex.ToString());
        }
    }

    /// <summary>
    /// Sync thread desktop with active input desktop. Called before every
    /// injection — если user нажал Win+L или сработал UAC, thread переключится
    /// на Winlogon secure desktop и injection пойдёт туда. Service работает
    /// как SYSTEM → имеет право на SetThreadDesktop для winsta0.
    /// </summary>
    private bool _initDesktopLogged;

    private void EnsureCurrentInputDesktop()
    {
        var newDesktop = OpenInputDesktop(0, false, GENERIC_ALL);
        if (newDesktop == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _log.Warn("InputAgent", $"open_input_desktop_failed err={err}");
            return;
        }

        if (!SetThreadDesktop(newDesktop))
        {
            var err = Marshal.GetLastWin32Error();
            // ERROR_INVALID_HANDLE (6) — thread's winstation != WinSta0
            // ERROR_BUSY (170) — thread owns windows (наш MTA worker не должен)
            _log.Warn("InputAgent", $"set_thread_desktop_failed err={err}");
            CloseDesktop(newDesktop);
            return;
        }

        if (_currentDesktop != IntPtr.Zero && _currentDesktop != newDesktop)
            CloseDesktop(_currentDesktop);
        _currentDesktop = newDesktop;

        if (!_initDesktopLogged)
        {
            _log.Info("InputAgent", "thread_desktop_set_first_time");
            _initDesktopLogged = true;
        }
    }

    // ── Injection primitives ─────────────────────────────────────────

    private int _mouseLogCounter;

    private void DoInjectMouse(string action, int x, int y, int button, int delta)
    {
        var a = action.ToLowerInvariant();
        bool ok;
        switch (a)
        {
            case "move":
                ok = SetCursorPos(x, y);
                break;
            case "down":
                SetCursorPos(x, y);
                SendMouseButton(button, down: true);
                ok = true;
                break;
            case "up":
                SetCursorPos(x, y);
                SendMouseButton(button, down: false);
                ok = true;
                break;
            case "click":
                SetCursorPos(x, y);
                SendMouseButton(button, down: true);
                SendMouseButton(button, down: false);
                ok = true;
                break;
            case "wheel":
                SetCursorPos(x, y);
                SendMouseInput(MOUSEEVENTF_WHEEL, delta);
                ok = true;
                break;
            default:
                ok = false;
                break;
        }

        // Логируем первый, каждый 60-й и failures. Иначе mouse move
        // (~60 Hz) забьёт лог полностью.
        var n = Interlocked.Increment(ref _mouseLogCounter);
        if (!ok || n == 1 || n % 60 == 0)
        {
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            _log.Debug("InputAgent", $"mouse_inject action={a} x={x} y={y} btn={button} ok={ok} err={err} seq={n}");
        }
    }

    private void DoInjectKeyboard(string action, int vk, int scanCode)
    {
        if (vk == 0) return;
        var virtualKey = (ushort)vk;
        var scan = (ushort)scanCode;
        var a = action.ToLowerInvariant();
        if (a is "down" or "press")
            SendKeyboardInput(virtualKey, scan, keyUp: false);
        if (a is "up" or "press")
            SendKeyboardInput(virtualKey, scan, keyUp: true);
    }

    private static void SendMouseButton(int button, bool down)
    {
        var flag = button switch
        {
            2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            3 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
        };
        SendMouseInput(flag, 0);
    }

    private static void SendMouseInput(uint flags, int mouseData)
    {
        INPUT[] inputs =
        [
            new INPUT
            {
                type = INPUT_MOUSE,
                Anonymous = new INPUT_UNION
                {
                    mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData }
                }
            }
        ];
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyboardInput(ushort virtualKey, ushort scanCode, bool keyUp)
    {
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0u;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        INPUT[] inputs =
        [
            new INPUT
            {
                type = INPUT_KEYBOARD,
                Anonymous = new INPUT_UNION
                {
                    ki = new KEYBDINPUT { wVk = virtualKey, wScan = scanCode, dwFlags = flags }
                }
            }
        ];
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort vk)
    {
        return vk is 0xA3 /*RCTRL*/ or 0xA5 /*RALT*/ or 0x5B /*LWIN*/ or 0x5C /*RWIN*/
            or 0x2D /*INS*/ or 0x2E /*DEL*/ or 0x24 /*HOME*/ or 0x23 /*END*/
            or 0x21 /*PGUP*/ or 0x22 /*PGDN*/
            or 0x25 or 0x26 or 0x27 or 0x28 /*arrows*/
            or 0x90 /*NUMLOCK*/ or 0x2C /*PRTSC*/ or 0x5D /*APPS*/ or 0x6F /*DIVIDE*/;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        try { _worker.Join(TimeSpan.FromSeconds(2)); } catch { }
        if (_currentDesktop != IntPtr.Zero)
        {
            try { CloseDesktop(_currentDesktop); } catch { }
            _currentDesktop = IntPtr.Zero;
        }
        _queue.Dispose();
        _cts.Dispose();
    }

    // ── P/Invoke ──

    private const uint GENERIC_ALL = 0x10000000;
    private const uint WINSTA_ALL_ACCESS = 0x0000037F;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUT_UNION Anonymous; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nuint dwExtraInfo;
    }
}

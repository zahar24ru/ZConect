using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

#pragma warning disable CA1416 // Windows-only P/Invokes

namespace ZConectInputHelper;

/// <summary>
/// Small console process spawned by ZConectService into the user's
/// interactive session with winlogon.exe token (= SYSTEM in session N).
///
/// Being in user session with SYSTEM-level access means:
///   * WinStation is session N's WinSta0 (not session 0's) → OpenInputDesktop
///     returns session N's active desktop (Default or Winlogon).
///   * SetThreadDesktop accepted for both Default and Winlogon desktops
///     in the same winstation.
///   * SendInput / SetCursorPos target session N physical screen — user
///     actually sees the result.
///
/// Receives inject_* commands from the service over a SYSTEM-only-ACL'd
/// named pipe. Emits desktop_changed back to the service when active input
/// desktop switches (user locking / UAC prompt / unlock).
/// </summary>
internal static class Program
{
    private const string DefaultPipeName = "ZConect_Input_Helper_IPC";
    // volatile — читается из InjectMouse (под _injectLock) и пишется из
    // DesktopPollLoopAsync (отдельный task). String reference assignment атомарен,
    // но volatile гарантирует немедленную видимость после write.
    private static volatile string _currentDesktop = "Default";
    private static readonly object _injectLock = new();
    private static NamedPipeServerStream? _activePipe;
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    // File log in ProgramData so и service и helper могут туда писать.
    // Helper is spawned CREATE_NO_WINDOW → stdout теряется; диагностика без
    // файла невозможна.
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ZConect", "logs", "input-helper.log");
    private static readonly object _logLock = new();
    private static int _injectionCounter;
    private static IntPtr _lastDesktopHandle = IntPtr.Zero;

    // Log rotation: 5 MB primary + до 3 rotated backup'а.
    // Без этого AppendAllText растёт бесконечно — за неделю непрерывной
    // работы файл становится гигабайтами.
    private const long MaxLogFileBytes = 5 * 1024 * 1024;
    private const int MaxLogBackups = 3;

    public static async Task<int> Main(string[] args)
    {
        // Ensure log directory exists. Helper spawned by service может fail
        // создать папку если её нет — rare.
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!); } catch { /* best effort */ }

        // DPI awareness. Без этого Windows virtualize'ит координаты: например,
        // на 3000x2000 экране с 150% scaling helper видит "2000x1333" и
        // SetCursorPos с native coords 2500,1500 попадает куда-то в 2/3
        // экрана. Viewer шлёт native coords, UI (WPF DPI-aware) не scale'ит —
        // мы должны быть DPI-aware тоже.
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                Log("INFO", "dpi_awareness_per_monitor_v2_set");
            else
                Log("WARN", $"set_dpi_awareness_failed err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log("WARN", $"dpi_awareness_exception: {ex.Message}"); }

        var pipeName = ParseArg(args, "--pipe-name") ?? DefaultPipeName;
        Log("INFO", $"helper_starting pipe={pipeName} pid={Environment.ProcessId} session={Process.GetCurrentProcess().SessionId}");

        // Identify our token so we know what access we have. Should be SYSTEM
        // in user session когда spawned с winlogon token.
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            Log("INFO", $"token_user={identity.User?.Value ?? "?"} name={identity.Name}");
        }
        catch (Exception ex) { Log("WARN", $"identity_check_exception: {ex.Message}"); }

        // Switch process to WinSta0 of our session. Required for
        // SetThreadDesktop to work on both Default and Winlogon.
        try
        {
            var winsta0 = OpenWindowStation("WinSta0", false, WINSTA_ALL_ACCESS);
            if (winsta0 == IntPtr.Zero)
                Log("WARN", $"open_winsta0_failed err={Marshal.GetLastWin32Error()}");
            else if (!SetProcessWindowStation(winsta0))
                Log("WARN", $"set_process_winstation_failed err={Marshal.GetLastWin32Error()}");
            else
                Log("INFO", "winstation_switched_to_winsta0");
        }
        catch (Exception ex) { Log("WARN", $"winstation_switch_exception: {ex.Message}"); }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Desktop polling — push changes back to service.
        var desktopPollTask = Task.Run(() => DesktopPollLoopAsync(cts.Token));

        // Pipe server loop — accept connections from service, handle messages.
        await PipeServerLoopAsync(pipeName, cts.Token);

        cts.Cancel();
        try { await desktopPollTask; } catch { }
        return 0;
    }

    // ── Pipe server ──────────────────────────────────────────────────

    private static async Task PipeServerLoopAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // ACL: only SYSTEM can connect (prevent non-service processes
                // from subverting input injection in user session).
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));

                pipe = NamedPipeServerStreamAcl.Create(
                    pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    inBufferSize: 0, outBufferSize: 0, sec);

                await pipe.WaitForConnectionAsync(ct);
                LogInfo("service_connected");

                _activePipe = pipe;
                await HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LogInfo($"pipe_server_error: {ex.Message}"); }
            finally
            {
                _activePipe = null;
                try { pipe?.Dispose(); } catch { }
                await Task.Delay(500, ct);
            }
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            var maybeMsg = await ReadMessageAsync(pipe, ct);
            if (maybeMsg is null) break;
            var msg = maybeMsg.Value;

            try
            {
                var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "inject_mouse":
                        InjectMouse(msg);
                        break;
                    case "inject_keyboard":
                        InjectKeyboard(msg);
                        break;
                    default:
                        LogInfo($"unknown_msg_type: {type}");
                        break;
                }
            }
            catch (Exception ex) { LogInfo($"handle_error: {ex.Message}"); }
        }
    }

    private static async Task<JsonElement?> ReadMessageAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        int read = 0;
        while (read < 4)
        {
            var n = await pipe.ReadAsync(header.AsMemory(read, 4 - read), ct);
            if (n == 0) return null;
            read += n;
        }
        var len = BitConverter.ToInt32(header, 0);
        if (len <= 0 || len > 1 * 1024 * 1024) return null;

        var payload = new byte[len];
        read = 0;
        while (read < len)
        {
            var n = await pipe.ReadAsync(payload.AsMemory(read, len - read), ct);
            if (n == 0) return null;
            read += n;
        }
        // JsonDocument.Parse аллоцирует native pooled memory — обязателен Dispose.
        // Clone() копирует в managed, после чего doc можно безопасно освободить.
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static async Task SendToServiceAsync(object message, CancellationToken ct)
    {
        var pipe = _activePipe;
        if (pipe is null || !pipe.IsConnected) return;
        var json = JsonSerializer.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(payload.Length);
        await _writeLock.WaitAsync(ct);
        try
        {
            if (!pipe.IsConnected) return;
            await pipe.WriteAsync(header, ct);
            await pipe.WriteAsync(payload, ct);
            await pipe.FlushAsync(ct);
        }
        catch { /* ignore, reconnect will retry */ }
        finally { _writeLock.Release(); }
    }

    // ── Desktop polling ─────────────────────────────────────────────

    private static async Task DesktopPollLoopAsync(CancellationToken ct)
    {
        var initial = GetInputDesktopName();
        if (initial is not null) _currentDesktop = initial;
        LogInfo($"desktop_monitor_started initial={_currentDesktop}");

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            var name = GetInputDesktopName();
            if (name is null) continue;
            if (!name.Equals(_currentDesktop, StringComparison.OrdinalIgnoreCase))
            {
                var old = _currentDesktop;
                _currentDesktop = name;
                LogInfo($"desktop_switch from={old} to={name}");
                await SendToServiceAsync(new { type = "desktop_changed", desktop = name }, ct);
            }
        }
    }

    private static string? GetInputDesktopName()
    {
        var hDesktop = OpenInputDesktop(0, false, GENERIC_ALL);
        if (hDesktop == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[256];
            if (GetUserObjectInformation(hDesktop, UOI_NAME, buffer, buffer.Length, out var needed))
            {
                var name = Encoding.Unicode.GetString(buffer, 0, needed).TrimEnd('\0');
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            return null;
        }
        finally { CloseDesktop(hDesktop); }
    }

    // ── Input injection ─────────────────────────────────────────────

    private static void InjectMouse(JsonElement msg)
    {
        var action = msg.TryGetProperty("inputAction", out var a) ? (a.GetString() ?? "move") : "move";
        var x = msg.TryGetProperty("inputX", out var xe) ? xe.GetInt32() : 0;
        var y = msg.TryGetProperty("inputY", out var ye) ? ye.GetInt32() : 0;
        var button = msg.TryGetProperty("inputButton", out var be) ? be.GetInt32() : 0;
        var delta = msg.TryGetProperty("inputDelta", out var de) ? de.GetInt32() : 0;

        lock (_injectLock)
        {
            if (!EnsureCurrentDesktop()) return; // desktop switch fail → инъекция пошла бы не туда
            DoInjectMouse(action, x, y, button, delta);
        }
    }

    private static void InjectKeyboard(JsonElement msg)
    {
        var action = msg.TryGetProperty("inputAction", out var a) ? (a.GetString() ?? "press") : "press";
        var vk = msg.TryGetProperty("inputVirtualKey", out var vke) ? vke.GetInt32() : 0;
        var sc = msg.TryGetProperty("inputScanCode", out var sce) ? sce.GetInt32() : 0;
        if (vk == 0) return;

        lock (_injectLock)
        {
            if (!EnsureCurrentDesktop()) return;
            var ac = action.ToLowerInvariant();
            if (ac is "down" or "press") SendKeyboardInput((ushort)vk, (ushort)sc, keyUp: false);
            if (ac is "up" or "press") SendKeyboardInput((ushort)vk, (ushort)sc, keyUp: true);
        }
    }

    private static bool _desktopSetLoggedDefault, _desktopSetLoggedWinlogon;

    /// <summary>
    /// Возвращает true если thread desktop успешно переключён на active input
    /// desktop. false → caller ДОЛЖЕН пропустить инъекцию: иначе SendInput
    /// попадёт на старый desktop (если был) и разлогинится в другом контексте.
    /// </summary>
    private static bool EnsureCurrentDesktop()
    {
        var desktop = OpenInputDesktop(0, false, GENERIC_ALL);
        if (desktop == IntPtr.Zero)
        {
            Log("WARN", $"open_input_desktop_failed err={Marshal.GetLastWin32Error()}");
            return false;
        }

        if (!SetThreadDesktop(desktop))
        {
            var err = Marshal.GetLastWin32Error();
            Log("WARN", $"set_thread_desktop_failed err={err}");
            CloseDesktop(desktop);
            return false;
        }

        // Log first successful switch per desktop type (Default vs Winlogon).
        // Subsequent same-desktop switches don't log — flood prevention.
        var name = GetDesktopName(desktop) ?? "?";
        var isWinlogon = name.StartsWith("Winlogon", StringComparison.OrdinalIgnoreCase);
        if (isWinlogon && !_desktopSetLoggedWinlogon)
        {
            Log("INFO", $"thread_desktop_set_first_time name={name}");
            _desktopSetLoggedWinlogon = true;
        }
        else if (!isWinlogon && !_desktopSetLoggedDefault)
        {
            Log("INFO", $"thread_desktop_set_first_time name={name}");
            _desktopSetLoggedDefault = true;
        }

        // Close previous desktop handle, keep new one for thread lifetime.
        if (_lastDesktopHandle != IntPtr.Zero && _lastDesktopHandle != desktop)
            CloseDesktop(_lastDesktopHandle);
        _lastDesktopHandle = desktop;
        return true;
    }

    private static string? GetDesktopName(IntPtr desktop)
    {
        var buffer = new byte[256];
        if (GetUserObjectInformation(desktop, UOI_NAME, buffer, buffer.Length, out var needed))
            return Encoding.Unicode.GetString(buffer, 0, needed).TrimEnd('\0');
        return null;
    }

    private static void DoInjectMouse(string action, int x, int y, int button, int delta)
    {
        var a = action.ToLowerInvariant();
        bool ok = true;
        switch (a)
        {
            case "move": ok = SetCursorPos(x, y); break;
            case "down":
                ok = SetCursorPos(x, y);
                SendMouseButton(button, down: true);
                break;
            case "up":
                ok = SetCursorPos(x, y);
                SendMouseButton(button, down: false);
                break;
            case "click":
                ok = SetCursorPos(x, y);
                SendMouseButton(button, down: true);
                SendMouseButton(button, down: false);
                break;
            case "wheel":
                ok = SetCursorPos(x, y);
                SendMouseInput(MOUSEEVENTF_WHEEL, delta);
                break;
        }

        // Log: first injection, all non-move, all failures, every 60th move
        // (flood prevention при active drag).
        var n = Interlocked.Increment(ref _injectionCounter);
        var isMove = a == "move";
        if (!ok || !isMove || n == 1 || n % 60 == 0)
        {
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            Log("DEBUG", $"mouse action={a} x={x} y={y} btn={button} ok={ok} err={err} seq={n} desktop={_currentDesktop}");
        }
    }

    private static void SendMouseButton(int button, bool down)
    {
        var flag = button switch
        {
            2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            3 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
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
                Anonymous = new INPUT_UNION { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData } }
            }
        ];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        // SendInput=0 означает что input заблокирован Windows (race с UIPI
        // barrier, secure desktop kernel filter, UAC switch mid-injection).
        // Логируем без crash — ввод молча теряется, viewer не получит
        // feedback, но хоть в логах увидим.
        if (sent == 0) Log("WARN", $"send_input_mouse_failed flags=0x{flags:X} err={Marshal.GetLastWin32Error()}");
    }

    private static void SendKeyboardInput(ushort vk, ushort scan, bool keyUp)
    {
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0u;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        INPUT[] inputs =
        [
            new INPUT
            {
                type = INPUT_KEYBOARD,
                Anonymous = new INPUT_UNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } }
            }
        ];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0) Log("WARN", $"send_input_kbd_failed vk=0x{vk:X} keyup={keyUp} err={Marshal.GetLastWin32Error()}");
    }

    private static bool IsExtendedKey(ushort vk) =>
        vk is 0xA3 or 0xA5 or 0x5B or 0x5C or 0x2D or 0x2E or 0x24 or 0x23
            or 0x21 or 0x22 or 0x25 or 0x26 or 0x27 or 0x28 or 0x90 or 0x2C or 0x5D or 0x6F;

    // ── Helpers ─────────────────────────────────────────────────────

    private static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void LogInfo(string msg) => Log("INFO", msg);

    private static void Log(string level, string msg)
    {
        var line = $"{{\"ts\":\"{DateTime.UtcNow:O}\",\"level\":\"{level}\",\"module\":\"InputHelper\",\"event_name\":\"{EscapeJson(msg)}\",\"error\":null}}";
        try
        {
            lock (_logLock)
            {
                RotateLogIfNeeded();
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch { /* disk full or permission — can't help, don't crash */ }
        // Also to stdout for diagnostic if someone captures it.
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {level} {msg}");
    }

    /// <summary>
    /// Если primary log превышает лимит — shift'ит backup'ы (.2→.3, .1→.2,
    /// main→.1, старейший .3 удаляется). Вызывается под _logLock.
    /// </summary>
    private static void RotateLogIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogFilePath);
            if (!fi.Exists || fi.Length < MaxLogFileBytes) return;

            for (var i = MaxLogBackups; i >= 1; i--)
            {
                var older = $"{LogFilePath}.{i}";
                var newer = i == 1 ? LogFilePath : $"{LogFilePath}.{i - 1}";
                try { if (File.Exists(older)) File.Delete(older); } catch { }
                try { if (File.Exists(newer)) File.Move(newer, older); } catch { }
            }
        }
        catch { /* rotation — best effort, не блокируем writes */ }
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    // ── P/Invoke ────────────────────────────────────────────────────

    private const uint GENERIC_ALL = 0x10000000;
    private const uint WINSTA_ALL_ACCESS = 0x0000037F;
    private const uint UOI_NAME = 2;
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, uint nIndex, byte[] pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    // DPI awareness — PER_MONITOR_AWARE_V2 (introduced Win10 1703).
    // Process receives raw pixel coords, не scaled.
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

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

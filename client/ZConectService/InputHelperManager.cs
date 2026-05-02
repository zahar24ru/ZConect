using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ZConectService;

/// <summary>
/// Spawns and manages ZConectInputHelper.exe in the active user session
/// under winlogon.exe token (SYSTEM-in-session-N). Helper process lives
/// in session N's WinSta0 — can SetThreadDesktop to both Default and
/// Winlogon, SendInput lands on the user's real screen.
///
/// Service ↔ helper communication via named pipe
/// (<see cref="HelperPipeName"/>). Service forwards inject_* commands
/// from UI to helper, relays helper's desktop_changed events to UI.
///
/// Lifecycle: spawned at service start, respawned if it exits, killed
/// on service stop.
/// </summary>
internal sealed class InputHelperManager : IDisposable
{
    public const string HelperPipeName = "ZConect_Input_Helper_IPC";

    private readonly ServiceLogger _log;
    private readonly string _helperExePath;
    private readonly PipeServer _mainPipeServer;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _processLock = new();
    private Process? _helperProcess;
    private NamedPipeClientStream? _helperPipe;
    private Task? _manageTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public InputHelperManager(ServiceLogger log, string helperExePath, PipeServer mainPipeServer)
    {
        _log = log;
        _helperExePath = helperExePath;
        _mainPipeServer = mainPipeServer;
    }

    public void Start(CancellationToken serviceCt)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceCt, _cts.Token);
        _manageTask = Task.Run(() => ManageLoopAsync(linked.Token));
    }

    /// <summary>Forward inject command from main UI pipe to input helper.</summary>
    public async Task ForwardInjectMouseAsync(string action, int x, int y, int button, int delta, CancellationToken ct)
    {
        await SendToHelperAsync(new
        {
            type = "inject_mouse",
            inputAction = action, inputX = x, inputY = y,
            inputButton = button, inputDelta = delta,
        }, ct);
    }

    public async Task ForwardInjectKeyboardAsync(string action, int vk, int sc, CancellationToken ct)
    {
        await SendToHelperAsync(new
        {
            type = "inject_keyboard",
            inputAction = action, inputVirtualKey = vk, inputScanCode = sc,
        }, ct);
    }

    // ── Management loop ─────────────────────────────────────────────

    private async Task ManageLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Проверяем helper под lock'ом — Dispose() может одновременно
                // обнулить _helperProcess; без sync возможен doule-dispose или
                // проверка stale reference'а.
                bool needSpawn;
                lock (_processLock) { needSpawn = _helperProcess is null || _helperProcess.HasExited; }
                if (needSpawn)
                {
                    await SpawnHelperAsync(ct);
                    bool spawnFailed;
                    lock (_processLock) { spawnFailed = _helperProcess is null; }
                    if (spawnFailed)
                    {
                        await Task.Delay(5000, ct);
                        continue;
                    }
                }

                // Connect pipe if not connected.
                if (_helperPipe is null || !_helperPipe.IsConnected)
                {
                    await ConnectHelperPipeAsync(ct);
                    if (_helperPipe is null || !_helperPipe.IsConnected)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }
                }

                // Read loop — receive desktop_changed events from helper.
                await ReadFromHelperLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn("InputHelper", $"manage_loop_error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task SpawnHelperAsync(CancellationToken ct)
    {
        await Task.Yield();

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFFu)
        {
            _log.Debug("InputHelper", "no_active_console_session_delayed");
            return;
        }

        if (!File.Exists(_helperExePath))
        {
            _log.Error("InputHelper", $"helper_exe_not_found path={_helperExePath}");
            return;
        }

        var winlogonPid = FindWinlogonPid(sessionId);
        if (winlogonPid == 0)
        {
            _log.Debug("InputHelper", $"winlogon_not_found session={sessionId}");
            return;
        }

        IntPtr hProc = IntPtr.Zero, procToken = IntPtr.Zero, dupToken = IntPtr.Zero, envBlock = IntPtr.Zero;
        try
        {
            hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, winlogonPid);
            if (hProc == IntPtr.Zero)
            {
                _log.Warn("InputHelper", $"open_process_failed err={Marshal.GetLastWin32Error()}");
                return;
            }

            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out procToken))
            {
                _log.Warn("InputHelper", $"open_process_token_failed err={Marshal.GetLastWin32Error()}");
                return;
            }

            if (!DuplicateTokenEx(procToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                SecurityImpersonation, TokenPrimary, out dupToken))
            {
                _log.Warn("InputHelper", $"duplicate_token_failed err={Marshal.GetLastWin32Error()}");
                return;
            }

            if (!CreateEnvironmentBlock(out envBlock, dupToken, false)) envBlock = IntPtr.Zero;

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"WinSta0\Default",
            };

            var cmdLine = $"\"{_helperExePath}\" --pipe-name \"{HelperPipeName}\"";
            if (!CreateProcessAsUser(dupToken, null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                false, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                envBlock, Path.GetDirectoryName(_helperExePath), ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                _log.Error("InputHelper", $"spawn_failed err={err} ({new Win32Exception(err).Message})");
                return;
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);

            try
            {
                var proc = Process.GetProcessById(pi.dwProcessId);
                lock (_processLock) { _helperProcess?.Dispose(); _helperProcess = proc; }
            }
            catch { lock (_processLock) { _helperProcess = null; } }

            _log.Info("InputHelper", $"helper_spawned pid={pi.dwProcessId} session={sessionId}");
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (procToken != IntPtr.Zero) CloseHandle(procToken);
            if (hProc != IntPtr.Zero) CloseHandle(hProc);
        }
    }

    private async Task ConnectHelperPipeAsync(CancellationToken ct)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", HelperPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, ct);
            _helperPipe = pipe;
            pipe = null; // ownership transferred to _helperPipe
            _log.Info("InputHelper", "pipe_connected_to_helper");
        }
        catch (Exception ex)
        {
            _log.Debug("InputHelper", $"pipe_connect_failed: {ex.Message}");
        }
        finally
        {
            // Если ConnectAsync бросил — pipe не был передан в _helperPipe, закрываем.
            pipe?.Dispose();
        }
    }

    private async Task ReadFromHelperLoopAsync(CancellationToken ct)
    {
        var pipe = _helperPipe;
        if (pipe is null) return;

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
                    case "desktop_changed":
                        var desktop = msg.TryGetProperty("desktop", out var d) ? d.GetString() : "Default";
                        _log.Info("InputHelper", $"desktop_changed_from_helper to={desktop}");
                        // Relay to UI via main service pipe.
                        await _mainPipeServer.PushDesktopChangedAsync(desktop ?? "Default");
                        break;
                    default:
                        _log.Debug("InputHelper", $"unknown_from_helper: {type}");
                        break;
                }
            }
            catch (Exception ex) { _log.Warn("InputHelper", $"helper_msg_error: {ex.Message}"); }
        }

        _helperPipe?.Dispose();
        _helperPipe = null;
        _log.Warn("InputHelper", "helper_pipe_disconnected");
    }

    private async Task SendToHelperAsync(object message, CancellationToken ct)
    {
        // Captured local против TOCTOU: ManageLoopAsync может обнулить/диспознуть
        // _helperPipe в параллельном потоке. Локальная копия держит ссылку пока
        // WriteAsync не завершится; если pipe disposed — ObjectDisposedException
        // явно handled.
        var pipe = _helperPipe;
        if (pipe is null || !pipe.IsConnected) return;

        var json = JsonSerializer.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(payload.Length);

        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(header, ct);
            await pipe.WriteAsync(payload, ct);
            await pipe.FlushAsync(ct);
        }
        catch (ObjectDisposedException) { /* disposed mid-write — ManageLoop переподключится */ }
        catch (IOException ex) { _log.Debug("InputHelper", $"send_to_helper_io_error: {ex.Message}"); }
        catch (Exception ex) { _log.Warn("InputHelper", $"send_to_helper_failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    private static async Task<JsonElement?> ReadMessageAsync(NamedPipeClientStream pipe, CancellationToken ct)
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
        // Зеркальный fix для C4 в ZConectInputHelper/Program.cs (external audit F-12).
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    private static int FindWinlogonPid(uint sessionId)
    {
        foreach (var proc in Process.GetProcessesByName("winlogon"))
        {
            try { if (proc.SessionId == (int)sessionId) return proc.Id; }
            catch { }
            finally { proc.Dispose(); }
        }
        return 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _manageTask?.Wait(1000); } catch { }
        try { _helperPipe?.Dispose(); } catch { }
        Process? proc;
        lock (_processLock) { proc = _helperProcess; _helperProcess = null; }
        try
        {
            if (proc is not null && !proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
            }
        }
        catch { }
        proc?.Dispose();
        _cts.Dispose();
    }

    // ── P/Invoke ────────────────────────────────────────────────────

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZConectService;

/// <summary>
/// Monitors Windows user sessions and spawns/manages the ZConnect helper process
/// in the active console session. Uses Win32 APIs (WTSQueryUserToken, CreateProcessAsUser).
/// </summary>
public sealed class SessionMonitor : IDisposable
{
    private readonly ServiceLogger _log;
    private readonly string _helperExePath;
    /// <summary>Set externally — when true, don't respawn helper after exit.</summary>
    public Func<bool>? ShouldSuppressRespawn { get; set; }
    private Process? _helperProcess;
    private uint _lastSessionId = uint.MaxValue;
    private DateTime _lastSpawnAttempt = DateTime.MinValue;

    // Crash-loop protection: if helper crashes too many times in a short window,
    // stop respawning to avoid burning CPU. Resets on session change or after cooldown.
    private const int RespawnDelayMs = 3000;
    private const int MaxRespawnsInWindow = 3;
    private static readonly TimeSpan RespawnWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CrashLoopCooldown = TimeSpan.FromMinutes(2);
    private readonly Queue<DateTime> _respawnTimestamps = new();
    private DateTime _crashLoopCooldownUntil = DateTime.MinValue;

    public SessionMonitor(ServiceLogger log, string helperExePath)
    {
        _log = log;
        _helperExePath = helperExePath;
    }

    /// <summary>Check active session and spawn/respawn helper as needed. Call every ~1 second.</summary>
    public void Tick()
    {
        var sessionId = WTSGetActiveConsoleSessionId();

        // Session changed (user logon/logoff/switch) — reset crash-loop counter.
        if (sessionId != _lastSessionId)
        {
            _log.Info("SessionMonitor", $"session_changed from={_lastSessionId} to={sessionId}");
            KillHelper();
            _lastSessionId = sessionId;
            _respawnTimestamps.Clear();
            _crashLoopCooldownUntil = DateTime.MinValue;

            if (sessionId != 0xFFFFFFFF) // valid session exists
            {
                // Persistent suppress check: если user ранее сделал Меню→Выход,
                // SuppressAutoSpawnUi=true в service-config. Не спавним UI на
                // reboot/login — user сам запустит через ярлык когда нужно.
                if (ShouldSuppressRespawn?.Invoke() == true)
                {
                    _log.Info("SessionMonitor", "initial_spawn_suppressed_user_preference");
                }
                else
                {
                    SpawnHelper(sessionId);
                }
            }
            return;
        }

        // Helper crashed — respawn with crash-loop protection.
        if (_helperProcess is not null && !IsProcessAlive(_helperProcess.Id))
        {
            _log.Warn("SessionMonitor", $"helper_exited pid={_helperProcess.Id}");
            _helperProcess.Dispose();
            _helperProcess = null;

            if (ShouldSuppressRespawn?.Invoke() == true)
            {
                _log.Info("SessionMonitor", "helper_respawn_suppressed_user_exit");
            }
            else if (DateTime.UtcNow < _crashLoopCooldownUntil)
            {
                // In cooldown after crash loop — don't respawn yet.
                _log.Debug("SessionMonitor", $"crash_loop_cooldown remaining={(_crashLoopCooldownUntil - DateTime.UtcNow).TotalSeconds:F0}s");
            }
            else if (sessionId != 0xFFFFFFFF && (DateTime.UtcNow - _lastSpawnAttempt).TotalMilliseconds > RespawnDelayMs)
            {
                // Evict old timestamps outside the window.
                while (_respawnTimestamps.Count > 0 && DateTime.UtcNow - _respawnTimestamps.Peek() > RespawnWindow)
                    _respawnTimestamps.Dequeue();

                if (_respawnTimestamps.Count >= MaxRespawnsInWindow)
                {
                    _crashLoopCooldownUntil = DateTime.UtcNow + CrashLoopCooldown;
                    _log.Error("SessionMonitor",
                        $"crash_loop_detected ({MaxRespawnsInWindow} crashes in {RespawnWindow.TotalMinutes:F0}m) — pausing respawn for {CrashLoopCooldown.TotalMinutes:F0}m");
                    _respawnTimestamps.Clear();
                }
                else
                {
                    _log.Info("SessionMonitor", "helper_respawning");
                    _respawnTimestamps.Enqueue(DateTime.UtcNow);
                    SpawnHelper(sessionId);
                }
            }
        }
        // Initial spawn was attempted but failed (e.g. WTSQueryUserToken error 1008
        // ERROR_NO_TOKEN — user ещё не завершил логин после session change). Повторяем
        // попытку с тем же throttling что для respawn; как только user token станет
        // доступным, spawn пройдёт. Без этого retry после reboot + fast session transition
        // UI никогда не запускался (видели в bug/logs-pc/logs на 2026-04-21).
        else if (_helperProcess is null && sessionId != 0xFFFFFFFF)
        {
            if (ShouldSuppressRespawn?.Invoke() == true) return;
            if (DateTime.UtcNow < _crashLoopCooldownUntil) return;
            if ((DateTime.UtcNow - _lastSpawnAttempt).TotalMilliseconds < RespawnDelayMs) return;

            _log.Debug("SessionMonitor", $"helper_retry_initial_spawn session={sessionId}");
            SpawnHelper(sessionId);
        }
    }

    private void SpawnHelper(uint sessionId)
    {
        _lastSpawnAttempt = DateTime.UtcNow;

        if (!File.Exists(_helperExePath))
        {
            _log.Error("SessionMonitor", $"helper_not_found path={_helperExePath}");
            return;
        }

        try
        {
            // Check if ZConnect.exe is already running in the target session (e.g. started manually).
            // F-10 (external audit 2026-04-18): сверяем exe path не только name. Attacker
            // может запустить процесс с именем "ZConnect.exe" но другим path'ом →
            // без path check мы бы приняли spoof как "already running" и не spawn'или
            // настоящий helper = DoS of remote desktop sessions.
            var expectedPath = Path.GetFullPath(_helperExePath);
            foreach (var proc in Process.GetProcessesByName("ZConnect"))
            {
                try
                {
                    if (proc.SessionId != (int)sessionId) continue;
                    // Zombie skip: GetProcessesByName может вернуть exiting process (WPF
                    // exit запустил DllMain detach который застрял в mrwebrtc —
                    // см. bug/logs-pc/windbg.txt от 2026-04-21). Такой proc имеет
                    // HasExited=true, но entry остаётся в system table пока handles
                    // не закрыты. Adopt'ить такой zombie приводит к бесконечному
                    // helper_already_running ↔ helper_exited циклу.
                    if (proc.HasExited)
                    {
                        _log.Debug("SessionMonitor", $"helper_candidate_skipped_zombie pid={proc.Id}");
                        continue;
                    }
                    string? actualPath = null;
                    try { actualPath = proc.MainModule?.FileName; } catch { /* access denied — cross-session MainModule read */ }
                    if (actualPath is null)
                    {
                        // MainModule недоступен (частая ситуация для cross-session процессов).
                        // Conservatively accept — reject'ить would break legitimate cases.
                        _log.Debug("SessionMonitor", $"helper_candidate_path_unreachable pid={proc.Id} — accepted by name");
                        _helperProcess = proc;
                        _log.Info("SessionMonitor", $"helper_already_running pid={proc.Id} session={sessionId}");
                        return;
                    }
                    if (string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _helperProcess = proc;
                        _log.Info("SessionMonitor", $"helper_already_running pid={proc.Id} session={sessionId}");
                        return;
                    }
                    _log.Warn("SessionMonitor", $"helper_candidate_path_mismatch pid={proc.Id} actual={actualPath} expected={expectedPath} — ignored");
                }
                catch { }
            }

            var pid = LaunchProcessInSession(sessionId, _helperExePath);
            if (pid > 0)
            {
                _helperProcess = Process.GetProcessById(pid);
                _log.Info("SessionMonitor", $"helper_spawned pid={pid} session={sessionId}");
            }
            else
            {
                _log.Error("SessionMonitor", $"helper_spawn_failed session={sessionId}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("SessionMonitor", $"helper_spawn_exception session={sessionId}", ex.Message);
        }
    }

    private void KillHelper()
    {
        if (_helperProcess is null) return;
        try
        {
            if (!_helperProcess.HasExited)
            {
                _log.Info("SessionMonitor", $"helper_killing pid={_helperProcess.Id}");
                _helperProcess.Kill(entireProcessTree: true);
                _helperProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("SessionMonitor", $"helper_kill_failed: {ex.Message}");
        }
        finally
        {
            _helperProcess.Dispose();
            _helperProcess = null;
        }
    }

    private bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) { _log?.Debug("SessionMonitor", $"process_check: {ex.Message}"); return false; } // process doesn't exist or access denied = not alive
    }

    /// <summary>Release helper reference without killing the process. Used on service stop.</summary>
    private void DetachHelper()
    {
        if (_helperProcess is null) return;
        _log.Info("SessionMonitor", $"helper_detached pid={_helperProcess.Id}");
        _helperProcess.Dispose();
        _helperProcess = null;
    }

    /// <summary>On service stop: detach (don't kill). GUI continues independently.</summary>
    public void Dispose() => DetachHelper();

    // ── Win32 P/Invoke ──────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel,
        int tokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

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

    /// <summary>Find winlogon.exe PID in the target session (runs as SYSTEM, has elevated token).</summary>
    private int FindWinlogonPid(uint sessionId)
    {
        foreach (var proc in Process.GetProcessesByName("winlogon"))
        {
            try
            {
                if (proc.SessionId == sessionId)
                    return proc.Id;
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return 0;
    }

    /// <summary>Launch a process in the specified user session. Returns PID or 0 on failure.</summary>
    private int LaunchProcessInSession(uint sessionId, string exePath)
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            // Получаем токен интерактивного user'а через WTSQueryUserToken — child
            // процесс наследует user SID, профиль, CurrentUser DPAPI keys, правильно
            // резолвится %APPDATA%. Раньше использовали winlogon.exe token (SYSTEM),
            // чтобы обойти UAC требование — но UiApp manifest уже asInvoker, поэтому
            // SYSTEM token не нужен и вреден: UI писал client-settings.json в
            // systemprofile AppData (недоступный пользователю), DPAPI шифровал под
            // SYSTEM key (нельзя расшифровать в обычной user session), pipe SID check
            // всегда фейлился (client SID = SYSTEM, active console = user).
            if (!WTSQueryUserToken(sessionId, out userToken) || userToken == IntPtr.Zero)
            {
                _log.Warn("SessionMonitor", $"WTSQueryUserToken_failed session={sessionId} error={Marshal.GetLastWin32Error()}");
                return 0;
            }

            // Duplicate token for CreateProcessAsUser (primary token required).
            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dupToken))
            {
                _log.Warn("SessionMonitor", $"DuplicateTokenEx_failed error={Marshal.GetLastWin32Error()}");
                return 0;
            }

            // Create environment block for the user.
            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
            {
                _log.Warn("SessionMonitor", $"CreateEnvironmentBlock_failed error={Marshal.GetLastWin32Error()}");
                envBlock = IntPtr.Zero;
            }

            // Prepare startup info — launch on user's default desktop.
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"WinSta0\Default"
            };

            var flags = CREATE_UNICODE_ENVIRONMENT;
            var cmdLine = $"\"{exePath}\" --from-service";

            if (!CreateProcessAsUser(dupToken, null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                    false, flags, envBlock, Path.GetDirectoryName(exePath), ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                _log.Error("SessionMonitor", $"CreateProcessAsUser_failed error={err} ({new Win32Exception(err).Message})");
                return 0;
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);

            return pi.dwProcessId;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }
}

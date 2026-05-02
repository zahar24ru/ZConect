using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZConectService;

/// <summary>
/// Запуск whitelisted процессов в active console session (user's default desktop)
/// из service SYSTEM context. Используется для "Task Manager" кнопки в viewer UI —
/// viewer шлёт DC request → host UI whitelists → pipe → ProcessLauncher запускает
/// taskmgr под токеном active console user'а.
///
/// Whitelist строгий: {"taskmgr"}. Новые allowed processes — только через code change +
/// review. Path hardcoded через Environment.SystemDirectory (anti-PATH-hijack).
/// </summary>
internal static class ProcessLauncher
{
    private static readonly Dictionary<string, string> AllowedProcesses = new(StringComparer.Ordinal)
    {
        // name → absolute path. SystemDirectory резолвится в C:\Windows\System32.
        { "taskmgr", Path.Combine(Environment.SystemDirectory, "taskmgr.exe") },
    };

    /// <summary>Запустить whitelisted процесс в active console session. Возвращает true при успехе.</summary>
    public static bool LaunchInActiveConsoleSession(string processName, ServiceLogger log)
    {
        if (!AllowedProcesses.TryGetValue(processName, out var exePath))
        {
            log.Warn("ProcessLauncher", $"launch_rejected_not_whitelisted name={processName}");
            return false;
        }
        if (!File.Exists(exePath))
        {
            log.Warn("ProcessLauncher", $"launch_failed_exe_missing path={exePath}");
            return false;
        }

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            log.Warn("ProcessLauncher", "launch_failed_no_active_console_session");
            return false;
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;

        try
        {
            // Токен интерактивного user'а — child-процесс наследует user SID, profile,
            // DPAPI CurrentUser keys, правильно резолвит %APPDATA%. Тот же pattern что
            // SessionMonitor.LaunchProcessInSession использует для UI.
            if (!WTSQueryUserToken(sessionId, out userToken) || userToken == IntPtr.Zero)
            {
                log.Warn("ProcessLauncher", $"WTSQueryUserToken_failed session={sessionId} error={Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dupToken))
            {
                log.Warn("ProcessLauncher", $"DuplicateTokenEx_failed error={Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!CreateEnvironmentBlock(out envBlock, dupToken, false))
            {
                log.Warn("ProcessLauncher", $"CreateEnvironmentBlock_failed error={Marshal.GetLastWin32Error()}");
                envBlock = IntPtr.Zero;
            }

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"WinSta0\Default"  // user's interactive desktop
            };

            var cmdLine = $"\"{exePath}\"";
            var workingDir = Path.GetDirectoryName(exePath);

            if (!CreateProcessAsUser(dupToken, null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                    false, CREATE_UNICODE_ENVIRONMENT, envBlock, workingDir, ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                log.Error("ProcessLauncher", $"CreateProcessAsUser_failed name={processName} error={err} ({new Win32Exception(err).Message})");
                return false;
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            log.Info("ProcessLauncher", $"launched name={processName} pid={pi.dwProcessId} session={sessionId}");
            return true;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

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

    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

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

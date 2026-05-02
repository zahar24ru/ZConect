using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZConectService;

/// <summary>
/// Помощник для impersonation winlogon.exe token'а активной user session.
/// Service в session 0 под SYSTEM token физически не видит user's session
/// window stations / desktops. Но winlogon.exe в user's session работает
/// SYSTEM-в-session-N, его token bound к правильной session. Impersonation
/// этим token'ом даёт service-thread'у session-specific context —
/// OpenInputDesktop возвращает user's active desktop, SetThreadDesktop
/// работает на его Winlogon.
/// </summary>
internal static class WinlogonImpersonation
{
    /// <summary>
    /// RAII-like scope: impersonates winlogon token на длительность using блока,
    /// RevertToSelf автоматически при Dispose.
    /// </summary>
    public readonly struct Scope : IDisposable
    {
        private readonly bool _impersonating;
        public bool Active => _impersonating;

        public Scope(bool impersonating) { _impersonating = impersonating; }

        public void Dispose()
        {
            if (_impersonating) RevertToSelf();
        }
    }

    /// <summary>
    /// Impersonate'ит winlogon.exe token активной console session. Возвращает
    /// scope — при Dispose делает RevertToSelf. Если не удалось — scope неактивен
    /// (Active==false), вызывающий code продолжает работу с собственным token'ом.
    /// </summary>
    public static Scope Enter(ServiceLogger? log = null)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFFu)
        {
            log?.Debug("Impersonation", "no_active_console_session");
            return new Scope(false);
        }

        var winlogonPid = FindWinlogonPid(sessionId);
        if (winlogonPid == 0)
        {
            log?.Debug("Impersonation", $"winlogon_not_found session={sessionId}");
            return new Scope(false);
        }

        IntPtr hProc = IntPtr.Zero;
        IntPtr procToken = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        try
        {
            hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, winlogonPid);
            if (hProc == IntPtr.Zero)
            {
                log?.Debug("Impersonation", $"open_process_failed err={Marshal.GetLastWin32Error()}");
                return new Scope(false);
            }

            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_IMPERSONATE, out procToken))
            {
                log?.Debug("Impersonation", $"open_process_token_failed err={Marshal.GetLastWin32Error()}");
                return new Scope(false);
            }

            if (!DuplicateTokenEx(procToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                SecurityImpersonation, TokenImpersonation, out dupToken))
            {
                log?.Debug("Impersonation", $"duplicate_token_failed err={Marshal.GetLastWin32Error()}");
                return new Scope(false);
            }

            if (!ImpersonateLoggedOnUser(dupToken))
            {
                log?.Debug("Impersonation", $"impersonate_failed err={Marshal.GetLastWin32Error()}");
                return new Scope(false);
            }

            return new Scope(true);
        }
        finally
        {
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (procToken != IntPtr.Zero) CloseHandle(procToken);
            if (hProc != IntPtr.Zero) CloseHandle(hProc);
        }
    }

    private static int FindWinlogonPid(uint sessionId)
    {
        foreach (var proc in Process.GetProcessesByName("winlogon"))
        {
            try
            {
                if (proc.SessionId == (int)sessionId) return proc.Id;
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return 0;
    }

    // ── P/Invoke ──

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_IMPERSONATE = 0x0004;
    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

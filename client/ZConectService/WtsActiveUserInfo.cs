using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ZConectService;

/// <summary>
/// Получение SID активного console-пользователя через WTS API.
/// Нужно для pipe-аутентификации: service в session 0 должен проверять
/// что подключающийся клиент — это текущий интерактивный пользователь,
/// а не другой locally-залогиненный user (RDP / Fast User Switching).
/// </summary>
internal static class WtsActiveUserInfo
{
    private const int WTS_CURRENT_SERVER_HANDLE = 0;

    /// <summary>
    /// Получить SID активного console session'а. Null если нет активного
    /// interactive session'а (например, в момент logout / boot).
    /// </summary>
    public static SecurityIdentifier? TryGetActiveConsoleUserSid()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        // 0xFFFFFFFF = нет активного console session'а.
        if (sessionId == 0xFFFFFFFFu) return null;

        IntPtr userToken = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken) || userToken == IntPtr.Zero)
                return null;

            using var windowsIdentity = new WindowsIdentity(userToken);
            return windowsIdentity.User;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (userToken != IntPtr.Zero)
                CloseHandle(userToken);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

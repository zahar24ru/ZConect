using System.Runtime.InteropServices;
using System.Text;

namespace UiApp.Services;

/// <summary>Centralized P/Invoke declarations for Windows desktop APIs.</summary>
internal static class DesktopInterop
{
    public const uint GENERIC_ALL = 0x10000000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, byte[] pvInfo, int nLength, out int lpnLengthNeeded);

    public const int UOI_NAME = 2;
}

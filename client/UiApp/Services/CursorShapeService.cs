using System.Runtime.InteropServices;

namespace UiApp.Services;

/// <summary>
/// Detects the current system cursor type using GetIconInfoExW.
/// Uses wResID (resource ID) which is desktop-independent —
/// works correctly after Winlogon desktop switch (unlike LoadCursor handle comparison).
/// </summary>
public static class CursorShapeService
{
    public static string GetCurrentCursorType()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.hCursor == IntPtr.Zero)
            return "arrow";

        // GetIconInfoExW returns wResID — the resource ID of the cursor.
        // For system cursors this is the IDC_ constant (32512=arrow, 32513=ibeam, etc.)
        // regardless of which desktop the handle came from.
        var iiex = new ICONINFOEXW { cbSize = (uint)Marshal.SizeOf<ICONINFOEXW>() };
        if (GetIconInfoExW(ci.hCursor, ref iiex))
        {
            // CRITICAL: GetIconInfoExW creates bitmap handles — must delete to avoid GDI leak.
            if (iiex.hbmMask != IntPtr.Zero) DeleteObject(iiex.hbmMask);
            if (iiex.hbmColor != IntPtr.Zero) DeleteObject(iiex.hbmColor);

            return MapResourceId(iiex.wResID);
        }

        return "arrow";
    }

    /// <summary>Map a cursor resource ID (wResID) to a string name. Returns "arrow" for unknown IDs.</summary>
    internal static string MapResourceId(ushort wResID)
    {
        return wResID switch
        {
            IDC_IBEAM      => "ibeam",
            IDC_HAND       => "hand",
            IDC_WAIT       => "wait",
            IDC_APPSTARTING => "appstarting",
            IDC_CROSS      => "cross",
            IDC_SIZEWE     => "sizewe",
            IDC_SIZENS     => "sizens",
            IDC_SIZENWSE   => "sizenwse",
            IDC_SIZENESW   => "sizenesw",
            IDC_SIZEALL    => "sizeall",
            IDC_NO         => "no",
            IDC_HELP       => "help",
            IDC_UPARROW    => "uparrow",
            _ => "arrow"
        };
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfoExW(IntPtr hIcon, ref ICONINFOEXW piconinfo);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const ushort IDC_ARROW = 32512;
    private const ushort IDC_IBEAM = 32513;
    private const ushort IDC_WAIT = 32514;
    private const ushort IDC_CROSS = 32515;
    private const ushort IDC_UPARROW = 32516;
    private const ushort IDC_SIZENWSE = 32642;
    private const ushort IDC_SIZENESW = 32643;
    private const ushort IDC_SIZEWE = 32644;
    private const ushort IDC_SIZENS = 32645;
    private const ushort IDC_SIZEALL = 32646;
    private const ushort IDC_NO = 32648;
    private const ushort IDC_HAND = 32649;
    private const ushort IDC_APPSTARTING = 32650;
    private const ushort IDC_HELP = 32651;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ICONINFOEXW
    {
        public uint cbSize;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
        public ushort wResID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szModName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szResName;
    }
}

using System.Runtime.InteropServices;

namespace UiApp.Services;

public static class CursorShapeService
{
    public static string GetCurrentCursorType()
    {
        var info = new CURSORINFO
        {
            cbSize = Marshal.SizeOf<CURSORINFO>()
        };
        if (!GetCursorInfo(ref info) || info.hCursor == IntPtr.Zero)
        {
            return "arrow";
        }

        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_IBEAM)) return "ibeam";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_HAND)) return "hand";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_WAIT)) return "wait";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_APPSTARTING)) return "appstarting";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_CROSS)) return "cross";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_SIZEWE)) return "sizewe";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_SIZENS)) return "sizens";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_SIZENWSE)) return "sizenwse";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_SIZENESW)) return "sizenesw";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_SIZEALL)) return "sizeall";
        if (info.hCursor == LoadCursor(IntPtr.Zero, IDC_NO)) return "no";
        return "arrow";
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    private const int IDC_ARROW = 32512;
    private const int IDC_IBEAM = 32513;
    private const int IDC_WAIT = 32514;
    private const int IDC_CROSS = 32515;
    private const int IDC_SIZEALL = 32646;
    private const int IDC_SIZENWSE = 32642;
    private const int IDC_SIZENESW = 32643;
    private const int IDC_SIZEWE = 32644;
    private const int IDC_SIZENS = 32645;
    private const int IDC_NO = 32648;
    private const int IDC_HAND = 32649;
    private const int IDC_APPSTARTING = 32650;

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
        public int x;
        public int y;
    }
}

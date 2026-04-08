using System.Runtime.InteropServices;
using Microsoft.Win32;
using WebRtcTransport;

namespace UiApp.Services;

public sealed class WindowsInputInjectionService
{
    public void InjectMouse(MouseInputPayload payload)
    {
        var action = payload.Action.Trim().ToLowerInvariant();
        switch (action)
        {
            case "move":
                SetCursorPos(payload.X, payload.Y);
                break;
            case "down":
                SetCursorPos(payload.X, payload.Y);
                SendMouseButton(payload.Button, down: true);
                break;
            case "up":
                SetCursorPos(payload.X, payload.Y);
                SendMouseButton(payload.Button, down: false);
                break;
            case "click":
                SetCursorPos(payload.X, payload.Y);
                SendMouseButton(payload.Button, down: true);
                SendMouseButton(payload.Button, down: false);
                break;
            case "wheel":
                SetCursorPos(payload.X, payload.Y);
                SendMouseInput(MOUSEEVENTF_WHEEL, payload.Delta);
                break;
        }
    }

    public void InjectKeyboard(KeyboardInputPayload payload)
    {
        var action = payload.Action.Trim().ToLowerInvariant();
        var virtualKey = (ushort)payload.VirtualKey;
        if (virtualKey == 0)
            return;

        var scanCode = (ushort)payload.ScanCode;

        // Viewer sends modifier keys (Ctrl, Shift, Alt, Win) as separate down/up events.
        // Do NOT re-inject modifiers from payload flags — that causes double-injection.
        if (action is "down" or "press")
            SendKeyboardInput(virtualKey, scanCode, keyUp: false);
        if (action is "up" or "press")
            SendKeyboardInput(virtualKey, scanCode, keyUp: true);
    }

    /// <summary>Send Ctrl+Alt+Del via SAS (Secure Attention Sequence). Requires SoftwareSASGeneration policy + admin.</summary>
    public static void SendCtrlAltDel()
    {
        try
        {
            EnsureSoftwareSasPolicy();
            SendSAS(asUser: false);
        }
        catch
        {
            // Fallback: ignored — SAS may not be available without SYSTEM/service context
        }
    }

    /// <summary>
    /// Ensures the SoftwareSASGeneration registry policy is set to allow SendSAS from applications.
    /// Value 1 = services, 3 = services + applications. Requires admin.
    /// </summary>
    private static void EnsureSoftwareSasPolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
            if (key is null) return;

            var current = key.GetValue("SoftwareSASGeneration");
            if (current is int val && val >= 1) return;

            key.SetValue("SoftwareSASGeneration", 3, RegistryValueKind.DWord);
        }
        catch
        {
            // Not admin — cannot set policy
        }
    }

    private static bool IsModifierVk(ushort vk)
    {
        return vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN
            or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU;
    }

    private static List<int> GetModifierVirtualKeys(KeyboardInputPayload payload)
    {
        var result = new List<int>(4);
        if (payload.Ctrl)  result.Add(VK_CONTROL);
        if (payload.Shift) result.Add(VK_SHIFT);
        if (payload.Alt)   result.Add(VK_MENU);
        if (payload.Win)   result.Add(VK_LWIN);
        return result;
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
                    mi = new MOUSEINPUT
                    {
                        dwFlags = flags,
                        mouseData = mouseData
                    }
                }
            }
        ];
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyboardInput(ushort virtualKey, ushort scanCode, bool keyUp)
    {
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        // Extended keys (right Ctrl/Alt, arrows, Ins/Del/Home/End/PgUp/PgDn, NumLock, Win, Apps, etc.)
        if (IsExtendedKey(virtualKey))
            flags |= KEYEVENTF_EXTENDEDKEY;

        INPUT[] inputs =
        [
            new INPUT
            {
                type = INPUT_KEYBOARD,
                Anonymous = new INPUT_UNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = scanCode,
                        dwFlags = flags
                    }
                }
            }
        ];
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort vk)
    {
        return vk is VK_RCONTROL or VK_RMENU or VK_LWIN or VK_RWIN
            or VK_INSERT or VK_DELETE or VK_HOME or VK_END
            or VK_PRIOR or VK_NEXT
            or VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN
            or VK_NUMLOCK or VK_SNAPSHOT or VK_APPS or VK_DIVIDE;
    }

    // ── P/Invoke ──

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("sas.dll", EntryPoint = "SendSAS")]
    private static extern void SendSAS(bool asUser);

    // ── Virtual Key constants ──

    private const int VK_SHIFT     = 0x10;
    private const int VK_CONTROL   = 0x11;
    private const int VK_MENU      = 0x12; // Alt
    private const int VK_LSHIFT    = 0xA0;
    private const int VK_RSHIFT    = 0xA1;
    private const int VK_LCONTROL  = 0xA2;
    private const int VK_RCONTROL  = 0xA3;
    private const int VK_LMENU     = 0xA4;
    private const int VK_RMENU     = 0xA5;
    private const int VK_LWIN      = 0x5B;
    private const int VK_RWIN      = 0x5C;

    private const ushort VK_INSERT   = 0x2D;
    private const ushort VK_DELETE   = 0x2E;
    private const ushort VK_HOME     = 0x24;
    private const ushort VK_END      = 0x23;
    private const ushort VK_PRIOR    = 0x21; // Page Up
    private const ushort VK_NEXT     = 0x22; // Page Down
    private const ushort VK_LEFT     = 0x25;
    private const ushort VK_RIGHT    = 0x27;
    private const ushort VK_UP       = 0x26;
    private const ushort VK_DOWN     = 0x28;
    private const ushort VK_NUMLOCK  = 0x90;
    private const ushort VK_SNAPSHOT = 0x2C; // PrintScreen
    private const ushort VK_APPS     = 0x5D; // Context Menu
    private const ushort VK_DIVIDE   = 0x6F; // Numpad /

    // ── Input flags ──

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP       = 0x0002;

    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_WHEEL      = 0x0800;

    // ── Structures ──

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION Anonymous;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }
}

using System.Runtime.InteropServices;
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
                SendMouseButton(payload.Button, down: true);
                break;
            case "up":
                SendMouseButton(payload.Button, down: false);
                break;
            case "click":
                SendMouseButton(payload.Button, down: true);
                SendMouseButton(payload.Button, down: false);
                break;
            case "wheel":
                SendMouseInput(MOUSEEVENTF_WHEEL, payload.Delta);
                break;
        }
    }

    public void InjectKeyboard(KeyboardInputPayload payload)
    {
        var action = payload.Action.Trim().ToLowerInvariant();
        var virtualKey = (ushort)payload.VirtualKey;
        if (virtualKey == 0)
        {
            return;
        }

        var modifiers = GetModifierVirtualKeys(payload);
        if (action is "down" or "press")
        {
            foreach (var modifier in modifiers)
            {
                SendKeyboardInput((ushort)modifier, keyUp: false);
            }

            SendKeyboardInput(virtualKey, keyUp: false);
        }

        if (action is "up" or "press")
        {
            SendKeyboardInput(virtualKey, keyUp: true);
            for (var i = modifiers.Count - 1; i >= 0; i--)
            {
                SendKeyboardInput((ushort)modifiers[i], keyUp: true);
            }
        }
    }

    private static List<int> GetModifierVirtualKeys(KeyboardInputPayload payload)
    {
        var result = new List<int>(3);
        if (payload.Ctrl)
        {
            result.Add(VK_CONTROL);
        }

        if (payload.Shift)
        {
            result.Add(VK_SHIFT);
        }

        if (payload.Alt)
        {
            result.Add(VK_MENU);
        }

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

    private static void SendKeyboardInput(ushort virtualKey, bool keyUp)
    {
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
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0
                    }
                }
            }
        ];

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION Anonymous;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
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

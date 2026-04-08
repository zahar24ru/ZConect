using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using UiApp.Services;
using WebRtcTransport;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    // ── Pressed key tracking (for release-all on disconnect) ─────────────────
    private readonly HashSet<int> _pressedVirtualKeys = new();

    // ── Serialized input sender (avoids Task.Run per event) ─────────────────
    private readonly Channel<object> _inputSendChannel = Channel.CreateBounded<object>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private Task? _inputSenderTask;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    private const uint MAPVK_VK_TO_VSC = 0;
    // ── Host-side: receive and inject input ───────────────────────────────────

    private void OnMouseInputReceived(MouseInputPayload payload)
    {
        if (_role != ConnectionRole.Host)
        {
            return;
        }
        try
        {
            _windowsInputInjectionService.InjectMouse(payload);
            if (payload.Action is not "move")
                _logService.Debug("InputHost", "mouse_injected_" + payload.Action);
        }
        catch (Exception ex)
        {
            _logService.Error("InputHost", "mouse_inject_failed", ex.Message);
        }
    }

    private void OnKeyboardInputReceived(KeyboardInputPayload payload)
    {
        if (_role != ConnectionRole.Host)
        {
            return;
        }
        try
        {
            _windowsInputInjectionService.InjectKeyboard(payload);
            _logService.Debug("InputHost", "keyboard_injected_" + payload.Action);
        }
        catch (Exception ex)
        {
            _logService.Error("InputHost", "keyboard_inject_failed", ex.Message);
        }
    }

    // ── Host-side: cursor shape sync ─────────────────────────────────────────

    private void StartCursorSyncLoopIfNeeded()
    {
        if (Interlocked.Exchange(ref _cursorLoopStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() => CursorSyncLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);
    }

    private async Task CursorSyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_role == ConnectionRole.Host)
                {
                    var cursorType = CursorShapeService.GetCurrentCursorType();
                    if (!string.Equals(cursorType, _lastSentCursorType, StringComparison.Ordinal))
                    {
                        var dc = _dataChannelCoordinator;
                        if (dc is not null)
                        {
                            await dc.SendCursorShapeAsync(new CursorShapePayload { CursorType = cursorType }, ct);
                            _lastSentCursorType = cursorType;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            try { await Task.Delay(120, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnCursorShapeReceived(CursorShapePayload payload)
    {
        if (_role != ConnectionRole.Viewer)
        {
            return;
        }

        var cursor = MapRemoteCursor(payload?.CursorType ?? "arrow");
        _uiContext.Post(_ => RemoteCursor = cursor, null);
    }

    private static Cursor MapRemoteCursor(string cursorType)
    {
        return cursorType.ToLowerInvariant() switch
        {
            "ibeam"      => Cursors.IBeam,
            "hand"       => Cursors.Hand,
            "wait"       => Cursors.Wait,
            "appstarting" => Cursors.AppStarting,
            "cross"      => Cursors.Cross,
            "sizewe"     => Cursors.SizeWE,
            "sizens"     => Cursors.SizeNS,
            "sizenwse"   => Cursors.SizeNWSE,
            "sizenesw"   => Cursors.SizeNESW,
            "sizeall"    => Cursors.SizeAll,
            "no"         => Cursors.No,
            _            => Cursors.Arrow
        };
    }

    // ── Viewer-side: capture and send input from remote screen surface ────────

    public void HandleRemoteSurfaceMouseMove(FrameworkElement surface, MouseEventArgs e)
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
        {
            return;
        }

        var nowMs = Environment.TickCount64;
        if (nowMs - _lastMouseMoveSentAtMs < 16)
        {
            return;
        }
        _lastMouseMoveSentAtMs = nowMs;

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
        {
            return;
        }

        if (_lastSentMouseX == x && _lastSentMouseY == y)
        {
            return;
        }
        _lastSentMouseX = x;
        _lastSentMouseY = y;

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "move", X = x, Y = y });
    }

    public void HandleRemoteSurfaceMouseDown(FrameworkElement surface, MouseButtonEventArgs e)
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
            return;

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
            return;

        var button = e.ChangedButton switch
        {
            MouseButton.Right  => 2,
            MouseButton.Middle => 3,
            _                  => 1
        };

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "down", Button = button, X = x, Y = y });
    }

    public void HandleRemoteSurfaceMouseUp(FrameworkElement surface, MouseButtonEventArgs e)
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
            return;

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
            return;

        var button = e.ChangedButton switch
        {
            MouseButton.Right  => 2,
            MouseButton.Middle => 3,
            _                  => 1
        };

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "up", Button = button, X = x, Y = y });
    }

    public void HandleRemoteSurfaceMouseWheel(FrameworkElement surface, MouseWheelEventArgs e)
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
        {
            return;
        }

        if (!TryMapSurfacePointToRemote(surface, e.GetPosition(surface), out var x, out var y))
        {
            return;
        }

        _ = SafeSendMouseAsync(new MouseInputPayload { Action = "wheel", X = x, Y = y, Delta = e.Delta });
    }

    public void HandleRemoteSurfaceKeyDown(KeyEventArgs e)
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
            return;

        var scanCode = (int)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);

        lock (_pressedVirtualKeys)
            _pressedVirtualKeys.Add(vk);

        _ = SafeSendKeyboardAsync(new KeyboardInputPayload
        {
            Action     = "down",
            VirtualKey = vk,
            ScanCode   = scanCode,
            Alt        = (Keyboard.Modifiers & ModifierKeys.Alt)     != 0,
            Ctrl       = (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            Shift      = (Keyboard.Modifiers & ModifierKeys.Shift)   != 0,
            Win        = (Keyboard.Modifiers & ModifierKeys.Windows) != 0
        });

        e.Handled = true;
    }

    public void HandleRemoteSurfaceKeyUp(KeyEventArgs e)
    {
        if (_role != ConnectionRole.Viewer || _dataChannelCoordinator is null)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
            return;

        var scanCode = (int)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);

        lock (_pressedVirtualKeys)
            _pressedVirtualKeys.Remove(vk);

        _ = SafeSendKeyboardAsync(new KeyboardInputPayload
        {
            Action     = "up",
            VirtualKey = vk,
            ScanCode   = scanCode,
            Alt        = (Keyboard.Modifiers & ModifierKeys.Alt)     != 0,
            Ctrl       = (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            Shift      = (Keyboard.Modifiers & ModifierKeys.Shift)   != 0,
            Win        = (Keyboard.Modifiers & ModifierKeys.Windows) != 0
        });
        e.Handled = true;
    }

    public void HandleRemoteSurfaceLostFocus()
    {
        _lastSentMouseX = null;
        _lastSentMouseY = null;
        ReleaseAllPressedKeys();
    }

    /// <summary>Release all tracked pressed keys on the remote host (prevents stuck keys).</summary>
    private void ReleaseAllPressedKeys()
    {
        int[] keysToRelease;
        lock (_pressedVirtualKeys)
        {
            if (_pressedVirtualKeys.Count == 0) return;
            keysToRelease = _pressedVirtualKeys.ToArray();
            _pressedVirtualKeys.Clear();
        }

        foreach (var vk in keysToRelease)
        {
            try
            {
                _ = SafeSendKeyboardAsync(new KeyboardInputPayload
                {
                    Action     = "up",
                    VirtualKey = vk,
                    ScanCode   = (int)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC)
                });
            }
            catch
            {
                // Ignore — data channel may already be closed during shutdown
            }
        }
    }

    // ── Ctrl+Alt+Del ──────────────────────────────────────────────────────────

    private async Task SendCtrlAltDelAsync()
    {
        if (_role == ConnectionRole.Host)
        {
            // Host presses locally
            WindowsInputInjectionService.SendCtrlAltDel();
            return;
        }

        // Viewer sends via data channel
        var dc = _dataChannelCoordinator;
        if (dc is null) return;
        try
        {
            await dc.SendCtrlAltDelAsync(_lifetimeCts.Token);
        }
        catch (Exception ex)
        {
            _logService.Error("Input", "send_ctrl_alt_del_failed", ex.Message);
        }
    }

    private void OnCtrlAltDelReceived()
    {
        if (_role != ConnectionRole.Host) return;
        try
        {
            WindowsInputInjectionService.SendCtrlAltDel();
            _logService.Debug("InputHost", "ctrl_alt_del_injected");
        }
        catch (Exception ex)
        {
            _logService.Error("InputHost", "ctrl_alt_del_failed", ex.Message);
        }
    }

    // ── Coordinate mapping ────────────────────────────────────────────────────

    private bool TryMapSurfacePointToRemote(FrameworkElement surface, Point p, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (_remoteFrameBitmap is null)
        {
            return false;
        }

        var meta     = _remoteScreenMeta;
        var captureX = meta?.CaptureX ?? 0;
        var captureY = meta?.CaptureY ?? 0;
        var captureW = meta?.Width    ?? _remoteFrameBitmap.PixelWidth;
        var captureH = meta?.Height   ?? _remoteFrameBitmap.PixelHeight;
        if (captureW <= 0 || captureH <= 0)
        {
            return false;
        }

        var surfaceW = surface.ActualWidth;
        var surfaceH = surface.ActualHeight;
        if (surfaceW <= 1 || surfaceH <= 1)
        {
            return false;
        }

        var frameW = (double)_remoteFrameBitmap.PixelWidth;
        var frameH = (double)_remoteFrameBitmap.PixelHeight;
        if (frameW <= 1 || frameH <= 1)
        {
            return false;
        }

        // Image использует Stretch=Uniform — считаем letterbox-отступы.
        var scale    = Math.Min(surfaceW / frameW, surfaceH / frameH);
        var displayW = frameW * scale;
        var displayH = frameH * scale;
        var offsetX  = (surfaceW - displayW) / 2;
        var offsetY  = (surfaceH - displayH) / 2;

        var rx = (p.X - offsetX) / scale;
        var ry = (p.Y - offsetY) / scale;
        if (rx < 0 || ry < 0 || rx >= frameW || ry >= frameH)
        {
            return false;
        }

        var sx = (int)Math.Round(rx * captureW / frameW);
        var sy = (int)Math.Round(ry * captureH / frameH);
        sx = Math.Clamp(sx, 0, captureW - 1);
        sy = Math.Clamp(sy, 0, captureH - 1);

        x = captureX + sx;
        y = captureY + sy;
        return true;
    }

    // ── Serialized input sender ─────────────────────────────────────────────

    private void EnsureInputSenderRunning()
    {
        if (_inputSenderTask is not null) return;
        _inputSenderTask = Task.Run(async () =>
        {
            await foreach (var item in _inputSendChannel.Reader.ReadAllAsync(_lifetimeCts.Token))
            {
                var dc = _dataChannelCoordinator;
                if (dc is null) continue;
                try
                {
                    if (item is MouseInputPayload mouse)
                        await dc.SendMouseAsync(mouse, _lifetimeCts.Token);
                    else if (item is KeyboardInputPayload kbd)
                        await dc.SendKeyboardAsync(kbd, _lifetimeCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });
    }

    private Task SafeSendMouseAsync(MouseInputPayload payload)
    {
        EnsureInputSenderRunning();
        _inputSendChannel.Writer.TryWrite(payload);
        return Task.CompletedTask;
    }

    private Task SafeSendKeyboardAsync(KeyboardInputPayload payload)
    {
        EnsureInputSenderRunning();
        _inputSendChannel.Writer.TryWrite(payload);
        return Task.CompletedTask;
    }
}

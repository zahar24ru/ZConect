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

    // F-06 fix: separate channels for keyboard (never drop) and mouse (drop oldest ok).
    private readonly Channel<KeyboardInputPayload> _keyboardSendChannel = Channel.CreateUnbounded<KeyboardInputPayload>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<MouseInputPayload> _mouseSendChannel = Channel.CreateBounded<MouseInputPayload>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private Task? _inputSenderTask;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    private const uint MAPVK_VK_TO_VSC = 0;
    // ── Host-side: receive and inject input ───────────────────────────────────

    private void OnMouseInputReceived(MouseInputPayload payload)
    {
        if (_role != ConnectionRole.Host) return;
        TouchP2PActivity();
        try
        {
            // ВСЕГДА через helper когда pipe доступен — helper работает под
            // winlogon token (System integrity). UIPI (User Interface Privilege
            // Isolation) blocks клики от Medium-integrity (UI user token) на
            // High/System integrity окна (UAC, Task Manager, Registry Editor
            // и т.д.). Helper's System integrity этим барьером не ограничен.
            //
            // Direct injection под user token оставляем только как fallback
            // для standalone режима без service — UIPI barrier тогда не
            // избежать, но хотя бы обычные клики работают.
            if (_servicePipe is not null && _servicePipe.IsConnected)
            {
                _ = _servicePipe.SendInjectMouseAsync(payload.Action, payload.X, payload.Y, payload.Button, payload.Delta);
            }
            else
            {
                SwitchCaptureThreadToActiveDesktop();
                _windowsInputInjectionService.InjectMouse(payload);
            }
            if (payload.Action is not "move")
                _logService.Debug("InputHost", $"mouse_injected_{payload.Action}_desktop={_cachedActiveDesktop}_via={( _servicePipe?.IsConnected == true ? "helper" : "direct")}");
        }
        catch (Exception ex)
        {
            _logService.Error("InputHost", "mouse_inject_failed", ex.Message);
        }
    }

    private void OnKeyboardInputReceived(KeyboardInputPayload payload)
    {
        if (_role != ConnectionRole.Host) return;
        try
        {
            // Same UIPI logic as mouse — always via helper when available.
            if (_servicePipe is not null && _servicePipe.IsConnected)
            {
                _ = _servicePipe.SendInjectKeyboardAsync(payload.Action, payload.VirtualKey, payload.ScanCode,
                    payload.Alt, payload.Ctrl, payload.Shift, payload.Win);
            }
            else
            {
                SwitchCaptureThreadToActiveDesktop();
                _windowsInputInjectionService.InjectKeyboard(payload);
            }
            _logService.Debug("InputHost", $"keyboard_injected_{payload.Action}_desktop={_cachedActiveDesktop}_via={( _servicePipe?.IsConnected == true ? "helper" : "direct")}");
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
                            _logService.Debug("Cursor", $"sent_{cursorType}");
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
            "help"       => Cursors.Help,
            "uparrow"    => Cursors.UpArrow,
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

    // ── Launch Process (Task Manager) ─────────────────────────────────────────
    // Viewer → Host: запрос запустить whitelisted процесс. Сейчас whitelist =
    // {"taskmgr"}. Trust boundary layers:
    //   1) Viewer ViewModel — hardcoded name, никакого user input в wire.
    //   2) Host ViewModel — whitelist re-check перед delegate в service (этот метод).
    //   3) Service (ProcessLauncher) — final whitelist в SYSTEM context.

    private static readonly HashSet<string> AllowedLaunchProcesses = new(StringComparer.Ordinal) { "taskmgr" };

    private async Task LaunchTaskManagerAsync()
    {
        if (_role != ConnectionRole.Viewer)
        {
            _logService.Debug("Launch", "launch_task_manager_ignored_not_viewer");
            return;
        }
        var dc = _dataChannelCoordinator;
        if (dc is null) return;
        try
        {
            await dc.SendLaunchProcessAsync(new LaunchProcessPayload { Name = "taskmgr" }, _lifetimeCts.Token);
            _logService.Info("Launch", "launch_task_manager_requested");
            // Гарантируем что окно удалённого экрана открыто — иначе viewer не
            // увидит запущенный taskmgr. Для FT-only mode событие no-op (окно
            // всё равно не откроется — там нет video).
            RemoteScreenWindowRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _logService.Error("Launch", "send_launch_process_failed", ex.Message);
        }
    }

    private void OnLaunchProcessRequestReceived(LaunchProcessPayload payload)
    {
        if (_role != ConnectionRole.Host) return;
        var name = payload.Name ?? string.Empty;
        if (!AllowedLaunchProcesses.Contains(name))
        {
            _logService.Warn("InputHost", $"launch_process_rejected_not_whitelisted name={name}");
            return;
        }
        _logService.Info("InputHost", $"launch_process_received_from_viewer name={name}");
        try
        {
            if (_servicePipe is not null)
            {
                _ = _servicePipe.SendInjectLaunchProcessAsync(name, _lifetimeCts.Token);
                _logService.Info("InputHost", "launch_process_delegated_to_service");
            }
            else
            {
                _logService.Warn("InputHost", "launch_process_no_service_pipe_cannot_launch");
            }
        }
        catch (Exception ex)
        {
            _logService.Error("InputHost", "launch_process_failed", ex.Message);
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

        var meta = _remoteScreenMeta;
        if (meta is null)
        {
            // Без screen_meta координаты всегда кривые: fallback на bitmap даёт
            // пространство кодированного кадра + origin (0,0), что не совпадает
            // с реальным screen/virtual desktop хоста. Блокируем input до прихода
            // screen_meta по Control DataChannel.
            if (!_mouseInputBlockedWarnedNoMeta)
            {
                _mouseInputBlockedWarnedNoMeta = true;
                _logService.Warn("RemoteScreen", "mouse_input_blocked_no_screen_meta");
            }
            return false;
        }
        var captureX = meta.CaptureX;
        var captureY = meta.CaptureY;
        var captureW = meta.Width;
        var captureH = meta.Height;
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
        var ct = _lifetimeCts.Token;
        _inputSenderTask = Task.Run(async () =>
        {
            // F-06 fix: process both channels, prioritizing keyboard (never dropped).
            var kbReader = _keyboardSendChannel.Reader;
            var mouseReader = _mouseSendChannel.Reader;
            while (!ct.IsCancellationRequested)
            {
                var dc = _dataChannelCoordinator;
                if (dc is null) { await Task.Delay(50, ct); continue; }
                try
                {
                    // Drain all pending keyboard events first (critical, never drop).
                    while (kbReader.TryRead(out var kbd))
                        await dc.SendKeyboardAsync(kbd, ct);
                    // Then process one mouse event.
                    if (mouseReader.TryRead(out var mouse))
                    {
                        await dc.SendMouseAsync(mouse, ct);
                        continue;
                    }
                    // Wait for either channel to have data.
                    await Task.WhenAny(kbReader.WaitToReadAsync(ct).AsTask(), mouseReader.WaitToReadAsync(ct).AsTask());
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });
    }

    private Task SafeSendMouseAsync(MouseInputPayload payload)
    {
        EnsureInputSenderRunning();
        _mouseSendChannel.Writer.TryWrite(payload);
        return Task.CompletedTask;
    }

    private Task SafeSendKeyboardAsync(KeyboardInputPayload payload)
    {
        EnsureInputSenderRunning();
        _keyboardSendChannel.Writer.TryWrite(payload);
        return Task.CompletedTask;
    }
}

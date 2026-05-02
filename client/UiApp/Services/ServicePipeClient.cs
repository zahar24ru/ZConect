using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UiApp.Services;

/// <summary>
/// Named Pipe client for communicating with ZConectService.
/// Helper (ZConnect.exe) connects to the service pipe on startup
/// to receive config and report status.
/// </summary>
public sealed class ServicePipeClient : IDisposable
{
    public const string DefaultPipeName = "ZConect_Service_IPC";
    private const int MaxReconnectDelayMs = 60_000; // cap at 60s
    private const int InitialReconnectDelayMs = 1_000; // start at 1s
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private readonly Action<string> _onLog;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private int _currentReconnectDelayMs = InitialReconnectDelayMs;

    /// <summary>True если UI запущен через ярлык (args без --from-service).
    /// Передаётся в hello message — service сбрасывает persistent SuppressAutoSpawnUi
    /// флаг только при явном user re-engage. Set from App.xaml.cs при startup.</summary>
    public bool LaunchedFromShortcut { get; set; }

    /// <summary>
    /// Serializes pipe writes. Named pipe semantics: каждый WriteAsync
    /// производит один пакет в stream. Но length-prefix protocol делает
    /// ДВА WriteAsync (header + payload) на одно логическое сообщение.
    /// Без lock'а concurrent SendAsync (например частые inject_mouse при
    /// движении) interleave'ят header и payload → service получает garbage
    /// JSON (видели 2026-04-18 при active mouse movement).
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Fired when config is received from service.</summary>
    public event Action<PipeConfigMessage>? ConfigReceived;

    /// <summary>Fired when desktop change notification arrives (UAC).</summary>
    public event Action<string>? DesktopChanged;

    public bool IsConnected => _pipe?.IsConnected == true;

    public ServicePipeClient(Action<string> onLog) : this(onLog, DefaultPipeName) { }

    public ServicePipeClient(Action<string> onLog, string pipeName)
    {
        _onLog = onLog;
        _pipeName = pipeName;
    }

    /// <summary>Try to connect to the service pipe. Returns true if connected.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool connected = false;
        try
        {
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(TimeoutConstants.PipeConnectTimeoutMs, ct);
            _onLog("pipe_connected_to_service");
            await SendAsync(new
            {
                type = "hello",
                pid = Environment.ProcessId,
                sessionId = Process.GetCurrentProcess().SessionId,
                launchedFromShortcut = LaunchedFromShortcut,
            }, ct);
            connected = true;
        }
        catch (TimeoutException)
        {
            _onLog("pipe_connect_timeout_service_not_running");
            _pipe?.Dispose();
            _pipe = null;
        }
        catch (Exception ex)
        {
            _onLog($"pipe_connect_failed: {ex.Message}");
            _pipe?.Dispose();
            _pipe = null;
        }

        // Always start reconnect loop — handles both initial failure and later disconnects.
        _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        return connected;
    }

    /// <summary>Report session status to service.</summary>
    public async Task ReportStatusAsync(bool connected, string? sessionId = null, string? loginCode = null, string? passCode = null)
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        try
        {
            await SendAsync(new
            {
                type = "status",
                connected,
                currentSessionId = sessionId ?? "",
                loginCode = loginCode ?? "",
                passCode = passCode ?? ""
            });
        }
        catch { /* best effort */ }
    }

    /// <summary>Tell service that user manually closed GUI — don't respawn.</summary>
    public async Task NotifyUserExitAsync()
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        try { await SendAsync(new { type = "user_exit" }); }
        catch { }
    }

    /// <summary>
    /// Synchronous вариант NotifyUserExitAsync — вызывается из nuclear ForceClose
    /// (MainWindow.xaml.cs). Async версия не работает: Task.Wait на UI thread
    /// deadlock'ается с async pipe write continuation'ом (tries to capture UI
    /// sync context, но UI заблокирован на Wait). Подтверждено 2026-04-21 в logs —
    /// service не получал user_exit → SessionMonitor respawn'ил UI через 800ms.
    ///
    /// Здесь всё sync: Write bytes → Flush → return. Kernel pipe buffers bytes,
    /// service async ReadAsync их подхватит через ms даже если мы вызовем
    /// TerminateProcess сразу после. Best-effort, swallow всё.
    /// </summary>
    public void NotifyUserExitSync()
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return;
        var locked = false;
        try
        {
            // Bounded lock acquire — если другой write в progress, ждём max 100ms.
            // При timeout всё равно пытаемся write (user_exit важнее чем perfectly
            // ordered pipe writes в shutdown path; race на short closing window ОК).
            try { locked = _writeLock.Wait(100); } catch { /* ignore */ }

            var envelope = new { type = "user_exit" };
            var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var payload = Encoding.UTF8.GetBytes(json);
            var header = BitConverter.GetBytes(payload.Length);  // 4 bytes LE

            pipe.Write(header, 0, 4);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
            _onLog("user_exit_sent_sync");
        }
        catch (Exception ex) { try { _onLog($"user_exit_sync_failed: {ex.Message}"); } catch { } }
        finally
        {
            if (locked) { try { _writeLock.Release(); } catch { } }
        }
    }

    /// <summary>
    /// Отправить обновлённые service-owned настройки (TURN credentials, STUN url)
    /// через pipe. Service получит, обновит свой service-config.json и
    /// новое значение заиграет на следующее подключение WebRTC.
    /// UI Settings без этого пути только пишет в client settings, что для
    /// TurnPassword бесполезно (service ничего не знает).
    /// </summary>
    public async Task UpdateServiceConfigAsync(
        string? turnUrl = null,
        string? turnUsername = null,
        string? turnPassword = null,
        string? stunUrl = null)
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        try
        {
            await SendAsync(new
            {
                type = "update_config",
                turnUrl = turnUrl ?? "",
                turnUsername = turnUsername ?? "",
                turnPassword = turnPassword ?? "",
                stunUrl = stunUrl ?? "",
            });
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Делегировать mouse-инъекцию в service (SYSTEM контекст). После перехода
    /// UI под user token (Stage 1) прямая инъекция через SetCursorPos не доходит
    /// до secure desktop (UAC). Service на SYSTEM thread'е умеет переключать
    /// thread desktop и инжектит напрямую.
    /// </summary>
    public async Task SendInjectMouseAsync(string action, int x, int y, int button, int delta, CancellationToken ct = default)
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        try
        {
            await SendAsync(new
            {
                type = "inject_mouse",
                inputAction = action,
                inputX = x,
                inputY = y,
                inputButton = button,
                inputDelta = delta,
            }, ct);
        }
        catch { /* best effort */ }
    }

    /// <summary>Делегировать keyboard-инъекцию в service (SYSTEM контекст).</summary>
    public async Task SendInjectKeyboardAsync(string action, int virtualKey, int scanCode, bool alt, bool ctrl, bool shift, bool win, CancellationToken ct = default)
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        try
        {
            await SendAsync(new
            {
                type = "inject_keyboard",
                inputAction = action,
                inputVirtualKey = virtualKey,
                inputScanCode = scanCode,
                inputAlt = alt,
                inputCtrl = ctrl,
                inputShift = shift,
                inputWin = win,
            }, ct);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Попросить service установить PromptOnSecureDesktop. UI под user
    /// token не имеет прав на HKLM\...\Policies\System, service под SYSTEM
    /// имеет. disable=true → UAC покажется на Default desktop (видно и
    /// кликается). disable=false → восстановить на Winlogon (secure).
    /// </summary>
    public async Task SetUacSecureDesktopAsync(bool disable, CancellationToken ct = default)
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        try
        {
            await SendAsync(new
            {
                type = "set_uac_policy",
                inputAction = disable ? "disable" : "enable",
            }, ct);
        }
        catch { /* best effort */ }
    }

    /// <summary>Делегировать запуск whitelisted процесса (сейчас "taskmgr") в service.
    /// Service enforce'ит whitelist повторно (defense-in-depth) перед CreateProcessAsUser.</summary>
    public async Task SendInjectLaunchProcessAsync(string processName, CancellationToken ct = default)
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        try { await SendAsync(new { type = "inject_launch_process", processName }, ct); }
        catch { /* best effort */ }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Read messages while connected.
                while (_pipe is not null && _pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var msg = await ReadMessageAsync(ct);
                    if (msg is null) break; // disconnected

                    switch (msg.Type)
                    {
                        case "config":
                            var maskedLogin = string.IsNullOrEmpty(msg.LoginCode) ? "none"
                                : msg.LoginCode.Length > 4 ? "****" + msg.LoginCode[^4..] : "****";
                            _onLog($"pipe_config_received login={maskedLogin}");
                            ConfigReceived?.Invoke(new PipeConfigMessage
                            {
                                LoginCode = msg.LoginCode ?? "",
                                PassCode = msg.PassCode ?? "",
                                SessionId = msg.CurrentSessionId ?? "",
                                SignalingUrl = msg.SignalingUrl ?? "",
                                WebSocketUrl = msg.WebSocketUrl ?? "",
                                StunUrl = msg.StunUrl ?? "",
                                TurnUrl = msg.TurnUrl ?? "",
                                TurnUsername = msg.TurnUsername ?? "",
                                TurnPassword = msg.TurnPassword ?? "",
                                MachineId = msg.MachineId ?? "",
                                DeviceSecret = msg.DeviceSecret ?? "",
                                UnattendedEnabled = msg.UnattendedEnabled,
                                OwnerSecret = msg.OwnerSecret ?? "",
                                WsUrl = msg.WsUrl ?? "",
                                WsToken = msg.WsToken ?? "",
                            });
                            break;

                        case "desktop_changed":
                            _onLog($"pipe_desktop_changed: {msg.Desktop}");
                            DesktopChanged?.Invoke(msg.Desktop ?? "Default");
                            break;

                        default:
                            _onLog($"pipe_unknown_message: {msg.Type}");
                            break;
                    }
                }

                // Disconnected — reconnect with exponential backoff.
                _onLog($"pipe_disconnected_will_reconnect delay={_currentReconnectDelayMs}ms");
                _pipe?.Dispose();
                _pipe = null;
                await Task.Delay(_currentReconnectDelayMs, ct);

                // Reconnect attempt.
                _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(TimeoutConstants.PipeConnectTimeoutMs, ct);
                _onLog("pipe_reconnected");
                _currentReconnectDelayMs = InitialReconnectDelayMs; // reset backoff on success
                await SendAsync(new
            {
                type = "hello",
                pid = Environment.ProcessId,
                sessionId = Process.GetCurrentProcess().SessionId,
                launchedFromShortcut = LaunchedFromShortcut,
            }, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException)
            {
                _onLog($"pipe_reconnect_timeout next_delay={_currentReconnectDelayMs}ms");
                _pipe?.Dispose();
                _pipe = null;
                // Exponential backoff: 1s → 2s → 4s → 8s → ... → 60s cap.
                _currentReconnectDelayMs = Math.Min(_currentReconnectDelayMs * 2, MaxReconnectDelayMs);
            }
            catch (Exception ex)
            {
                _onLog($"pipe_reconnect_error: {ex.Message} next_delay={_currentReconnectDelayMs}ms");
                _pipe?.Dispose();
                _pipe = null;
                _currentReconnectDelayMs = Math.Min(_currentReconnectDelayMs * 2, MaxReconnectDelayMs);
                try { await Task.Delay(_currentReconnectDelayMs, ct); } catch { break; }
            }
        }
    }

    /// <summary>Send a JSON message. Wire format: [4-byte LE length][UTF-8 JSON].
    /// Matches ZConectService.PipeProtocol format.</summary>
    private async Task SendAsync(object message, CancellationToken ct = default)
    {
        // Captured local предотвращает TOCTOU: если ReadLoopAsync обнулит _pipe
        // между check'ом и WriteAsync, local reference остаётся валидным (даже
        // если disposed — WriteAsync бросит ObjectDisposedException, handled below).
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return;

        var json = JsonSerializer.Serialize(message);
        var payload = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(payload.Length);

        // Держим lock на оба Write'а чтобы не interleave'ить header и payload
        // с параллельными SendAsync (mouse move шлётся ≈60 раз/сек).
        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(header, ct);
            await pipe.WriteAsync(payload, ct);
            await pipe.FlushAsync(ct);
        }
        catch (ObjectDisposedException) { /* pipe disposed mid-write — silent drop */ }
        catch (IOException ex) { _onLog($"pipe_write_io_error: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    private async Task<PipeMessageRead?> ReadMessageAsync(CancellationToken ct)
    {
        if (_pipe is null) return null;
        var header = new byte[4];
        if (await ReadExactAsync(header, ct) < 4) return null;
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 1024 * 1024) return null;
        var payload = new byte[length];
        if (await ReadExactAsync(payload, ct) < length) return null;
        return JsonSerializer.Deserialize<PipeMessageRead>(Encoding.UTF8.GetString(payload),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private async Task<int> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _pipe!.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) return offset;
            offset += read;
        }
        return offset;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _pipe?.Dispose();
        _pipe = null;
        _cts?.Dispose();
    }
}

/// <summary>Config message received from service.</summary>
public sealed class PipeConfigMessage
{
    public string LoginCode { get; init; } = "";
    public string PassCode { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string SignalingUrl { get; init; } = "";
    public string WebSocketUrl { get; init; } = "";
    public string StunUrl { get; init; } = "";
    public string TurnUrl { get; init; } = "";
    public string TurnUsername { get; init; } = "";
    public string TurnPassword { get; init; } = "";
    public string MachineId { get; init; } = "";
    public string DeviceSecret { get; init; } = ""; // proof of device ownership — required for session reuse
    public bool UnattendedEnabled { get; init; }
    // Session details — allows UI to skip HTTP CreateSession and connect WS directly.
    public string OwnerSecret { get; init; } = "";
    public string WsUrl { get; init; } = "";
    public string WsToken { get; init; } = "";
}

/// <summary>Deserialization model for pipe messages.</summary>
public sealed class PipeMessageRead
{
    public string Type { get; set; } = "";
    public string? SignalingUrl { get; set; }
    public string? WebSocketUrl { get; set; }
    public string? StunUrl { get; set; }
    public string? TurnUrl { get; set; }
    public string? TurnUsername { get; set; }
    public string? TurnPassword { get; set; }
    public string? MachineId { get; set; }
    public string? DeviceSecret { get; set; }
    public bool UnattendedEnabled { get; set; }
    public string? Desktop { get; set; }
    public string? LoginCode { get; set; }
    public string? PassCode { get; set; }
    public string? CurrentSessionId { get; set; }
    public string? OwnerSecret { get; set; }
    public string? WsUrl { get; set; }
    public string? WsToken { get; set; }
}

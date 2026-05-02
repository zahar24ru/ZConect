using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ZConectService;

/// <summary>
/// Simple length-prefixed JSON protocol for Named Pipe IPC between service and helper.
/// Wire format: [4 bytes LE length] [UTF-8 JSON payload]
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "ZConect_Service_IPC";

    /// <summary>Send a JSON message over the pipe.</summary>
    public static async Task SendAsync(PipeStream pipe, object message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var payload = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(payload.Length); // 4 bytes LE
        await pipe.WriteAsync(header, ct);
        await pipe.WriteAsync(payload, ct);
        await pipe.FlushAsync(ct);
    }

    /// <summary>Read a JSON message from the pipe. Returns null on disconnect.</summary>
    public static async Task<PipeMessage?> ReadAsync(PipeStream pipe, CancellationToken ct = default)
    {
        // Read 4-byte length header.
        var header = new byte[4];
        var headerRead = await ReadExactAsync(pipe, header, ct);
        if (headerRead < 4) return null; // disconnected

        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 1024 * 1024) return null; // invalid or too large

        // Read payload.
        var payload = new byte[length];
        var payloadRead = await ReadExactAsync(pipe, payload, ct);
        if (payloadRead < length) return null;

        var json = Encoding.UTF8.GetString(payload);
        return JsonSerializer.Deserialize<PipeMessage>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static async Task<int> ReadExactAsync(PipeStream pipe, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) return offset; // pipe closed
            offset += read;
        }
        return offset;
    }
}

/// <summary>Base message for pipe IPC. Type field determines the message kind.</summary>
public sealed class PipeMessage
{
    public string Type { get; set; } = string.Empty;

    // hello (helper → service)
    public int Pid { get; set; }
    public int SessionId { get; set; }
    /// <summary>True если UI запустил пользователь через ярлык (args без --from-service).
    /// False если UI spawned сервисом. Используется чтобы сбрасывать persistent
    /// SuppressAutoSpawnUi флаг только при явном user re-engage, не при
    /// service-initiated spawn.</summary>
    public bool LaunchedFromShortcut { get; set; }

    // status (helper → service)
    public bool Connected { get; set; }
    public string? CurrentSessionId { get; set; }
    public string? LoginCode { get; set; }
    public string? PassCode { get; set; }

    // config (service → helper)
    public string? SignalingUrl { get; set; }
    public string? WebSocketUrl { get; set; }
    public string? StunUrl { get; set; }
    public string? TurnUrl { get; set; }
    public string? TurnUsername { get; set; }
    public string? TurnPassword { get; set; }
    public string? MachineId { get; set; }
    public string? DeviceSecret { get; set; } // proof of device ownership for session reuse
    public bool UnattendedEnabled { get; set; }
    // Session details — allows UI to skip HTTP CreateSession and connect WS directly.
    public string? OwnerSecret { get; set; }
    public string? WsUrl { get; set; }
    public string? WsToken { get; set; }

    // desktop_changed (service → helper) — for UAC Phase 2
    public string? Desktop { get; set; }

    // inject_mouse / inject_keyboard / inject_launch_process (helper → service)
    // UI после перехода на user token больше не имеет SYSTEM прав для input
    // injection на secure desktop (UAC). Делегирует в service, который работает
    // как SYSTEM и может SetThreadDesktop в Winlogon.
    public string? InputAction { get; set; }       // "move", "down", "up", "click", "wheel" для mouse; "down", "up", "press" для keyboard
    public int InputX { get; set; }                // mouse X (absolute)
    public int InputY { get; set; }                // mouse Y (absolute)
    public int InputButton { get; set; }           // mouse button code
    public int InputDelta { get; set; }            // wheel delta
    public int InputVirtualKey { get; set; }       // keyboard VK code
    public int InputScanCode { get; set; }         // keyboard scan code
    public bool InputAlt { get; set; }             // modifier flag (info-only)
    public bool InputCtrl { get; set; }
    public bool InputShift { get; set; }
    public bool InputWin { get; set; }

    // inject_launch_process (helper → service) — запуск whitelisted процесса
    // (сейчас только "taskmgr") в active console session. Service выполняет
    // whitelist check в ProcessLauncher перед CreateProcessAsUser.
    public string? ProcessName { get; set; }
}

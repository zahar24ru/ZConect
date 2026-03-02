using System.Text.Json;

namespace WebRtcTransport;

public sealed class DataChannelCoordinator
{
    private readonly IDataChannelAgent _dataAgent;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Action<string> _onLog;

    public event Action<ClipboardTextPayload>? ClipboardReceived;
    public event Action<MouseInputPayload>? MouseReceived;
    public event Action<KeyboardInputPayload>? KeyboardReceived;
    public event Action<ScreenMetaPayload>? ScreenMetaReceived;
    public event Action<HostVideoSettingsRequestPayload>? HostVideoSettingsRequestReceived;
    public event Action<CursorShapePayload>? CursorShapeReceived;
    public event Action<HostDisplaysPayload>? HostDisplaysReceived;
    public event Action<HostDisplaysRequestPayload>? HostDisplaysRequestReceived;
    public event Action<FileMetaPayload>? FileMetaReceived;
    public event Action<FileChunkPayload>? FileChunkReceived;
    public event Action<FileEndPayload>? FileEndReceived;

    public DataChannelCoordinator(IDataChannelAgent dataAgent, Action<string> onLog)
    {
        _dataAgent = dataAgent;
        _onLog = onLog;
        _dataAgent.MessageReceived += OnMessageReceived;
    }

    public Task SendClipboardAsync(ClipboardTextPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Clipboard, "clipboard_text", payload, ct);

    public Task SendMouseAsync(MouseInputPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Input, "mouse_input", payload, ct);

    public Task SendKeyboardAsync(KeyboardInputPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Input, "keyboard_input", payload, ct);

    public Task SendScreenMetaAsync(ScreenMetaPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "screen_meta", payload, ct);

    public Task SendHostVideoSettingsRequestAsync(HostVideoSettingsRequestPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "host_video_settings_request", payload, ct);

    public Task SendCursorShapeAsync(CursorShapePayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "cursor_shape", payload, ct);

    public Task SendHostDisplaysAsync(HostDisplaysPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "host_displays", payload, ct);

    public Task SendHostDisplaysRequestAsync(HostDisplaysRequestPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "host_displays_request", payload, ct);

    public Task SendFileMetaAsync(FileMetaPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.File, "file_meta", payload, ct);

    public Task SendFileChunkAsync(FileChunkPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.File, "file_chunk", payload, ct);

    public Task SendFileEndAsync(FileEndPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.File, "file_end", payload, ct);

    private async Task SendAsync(DataChannelKind kind, string type, object payload, CancellationToken ct)
    {
        var envelope = new DataChannelEnvelope
        {
            Type = type,
            Payload = JsonSerializer.SerializeToElement(payload, _jsonOptions)
        };
        var raw = JsonSerializer.Serialize(envelope, _jsonOptions);
        await _dataAgent.SendTextAsync(kind, raw, ct);
        _onLog($"dc_sent:{kind}:{type}");
    }

    private void OnMessageReceived(DataChannelKind kind, string raw)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<DataChannelEnvelope>(raw, _jsonOptions);
            if (envelope is null)
            {
                return;
            }

            switch (envelope.Type)
            {
                case "clipboard_text":
                    ClipboardReceived?.Invoke(envelope.Payload.Deserialize<ClipboardTextPayload>(_jsonOptions) ?? new ClipboardTextPayload());
                    break;
                case "mouse_input":
                    MouseReceived?.Invoke(envelope.Payload.Deserialize<MouseInputPayload>(_jsonOptions) ?? new MouseInputPayload());
                    break;
                case "keyboard_input":
                    KeyboardReceived?.Invoke(envelope.Payload.Deserialize<KeyboardInputPayload>(_jsonOptions) ?? new KeyboardInputPayload());
                    break;
                case "screen_meta":
                    ScreenMetaReceived?.Invoke(envelope.Payload.Deserialize<ScreenMetaPayload>(_jsonOptions) ?? new ScreenMetaPayload());
                    break;
                case "host_video_settings_request":
                    HostVideoSettingsRequestReceived?.Invoke(
                        envelope.Payload.Deserialize<HostVideoSettingsRequestPayload>(_jsonOptions) ?? new HostVideoSettingsRequestPayload());
                    break;
                case "cursor_shape":
                    CursorShapeReceived?.Invoke(envelope.Payload.Deserialize<CursorShapePayload>(_jsonOptions) ?? new CursorShapePayload());
                    break;
                case "host_displays":
                    HostDisplaysReceived?.Invoke(envelope.Payload.Deserialize<HostDisplaysPayload>(_jsonOptions) ?? new HostDisplaysPayload());
                    break;
                case "host_displays_request":
                    HostDisplaysRequestReceived?.Invoke(
                        envelope.Payload.Deserialize<HostDisplaysRequestPayload>(_jsonOptions) ?? new HostDisplaysRequestPayload());
                    break;
                case "file_meta":
                    FileMetaReceived?.Invoke(envelope.Payload.Deserialize<FileMetaPayload>(_jsonOptions) ?? new FileMetaPayload());
                    break;
                case "file_chunk":
                    FileChunkReceived?.Invoke(envelope.Payload.Deserialize<FileChunkPayload>(_jsonOptions) ?? new FileChunkPayload());
                    break;
                case "file_end":
                    FileEndReceived?.Invoke(envelope.Payload.Deserialize<FileEndPayload>(_jsonOptions) ?? new FileEndPayload());
                    break;
                default:
                    _onLog("dc_unknown_type:" + envelope.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _onLog("dc_parse_error:" + ex.Message);
        }
    }
}

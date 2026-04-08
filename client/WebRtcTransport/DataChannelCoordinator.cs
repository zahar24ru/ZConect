using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace WebRtcTransport;

public sealed class DataChannelCoordinator
{
    // Binary chunk wire format:
    //   [0]      = 0x01 marker
    //   [1..4]   = sequence (int32 LE)
    //   [5..36]  = transferId (32 ASCII chars, Guid "N" format)
    //   [37]     = flags: 0x01 = compressed (GZip)
    //   [38..]   = raw file data
    private const byte BinaryChunkMarker = 0x01;
    private const int BinaryHeaderSize = 38; // 1 + 4 + 32 + 1

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
    /// <summary>Binary file chunk received (transferId, sequence, compressed flag, raw data).</summary>
    public event Action<string, int, bool, byte[]>? BinaryFileChunkReceived;
    public event Action<FileEndPayload>? FileEndReceived;
    public event Action<FileAckPayload>? FileAckReceived;
    public event Action<FileErrorPayload>? FileErrorReceived;
    public event Action<DirListRequestPayload>? DirListRequestReceived;
    public event Action<DirListResponsePayload>? DirListResponseReceived;
    public event Action<FileRequestPayload>? FileRequestReceived;
    public event Action<CreateFolderRequestPayload>? CreateFolderRequestReceived;
    public event Action<CreateFolderResponsePayload>? CreateFolderResponseReceived;
    public event Action<DeleteRequestPayload>? DeleteRequestReceived;
    public event Action<DeleteResponsePayload>? DeleteResponseReceived;
    public event Action<PingPayload>? PingReceived;
    public event Action<PongPayload>? PongReceived;
    public event Action? CtrlAltDelReceived;

    public DataChannelCoordinator(IDataChannelAgent dataAgent, Action<string> onLog)
    {
        _dataAgent = dataAgent;
        _onLog = onLog;
        _dataAgent.MessageReceived += OnMessageReceived;
        _dataAgent.BinaryMessageReceived += OnBinaryMessageReceived;
    }

    /// <summary>Subscribe to the underlying data channel open events.</summary>
    public void SubscribeChannelOpened(Action<DataChannelKind> handler) =>
        _dataAgent.ChannelOpened += handler;

    /// <summary>Unsubscribe from data channel open events.</summary>
    public void UnsubscribeChannelOpened(Action<DataChannelKind> handler) =>
        _dataAgent.ChannelOpened -= handler;

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

    /// <summary>Send file chunk as raw binary (no JSON, no Base64). ~0% overhead.</summary>
    public Task SendBinaryFileChunkAsync(string transferId, int sequence, byte[] data, bool compressed, CancellationToken ct = default)
    {
        var packet = new byte[BinaryHeaderSize + data.Length];
        packet[0] = BinaryChunkMarker;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1, 4), sequence);
        Encoding.ASCII.GetBytes(transferId.PadRight(32)[..32], 0, 32, packet, 5);
        packet[37] = compressed ? (byte)0x01 : (byte)0x00;
        Buffer.BlockCopy(data, 0, packet, BinaryHeaderSize, data.Length);
        return _dataAgent.SendBinaryAsync(DataChannelKind.File, packet, ct);
    }

    public Task SendFileEndAsync(FileEndPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.File, "file_end", payload, ct);

    public Task SendFileAckAsync(FileAckPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.File, "file_ack", payload, ct);

    public Task SendFileErrorAsync(FileErrorPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.File, "file_error", payload, ct);

    public Task SendDirListRequestAsync(DirListRequestPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "dir_list_request", payload, ct);

    public Task SendDirListResponseAsync(DirListResponsePayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "dir_list_response", payload, ct);

    public Task SendFileRequestAsync(FileRequestPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "file_request", payload, ct);

    public Task SendCreateFolderRequestAsync(CreateFolderRequestPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "create_folder_request", payload, ct);

    public Task SendCreateFolderResponseAsync(CreateFolderResponsePayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "create_folder_response", payload, ct);

    public Task SendDeleteRequestAsync(DeleteRequestPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "delete_request", payload, ct);

    public Task SendDeleteResponseAsync(DeleteResponsePayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "delete_response", payload, ct);

    public Task SendPingAsync(PingPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "ping", payload, ct);

    public Task SendPongAsync(PongPayload payload, CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "pong", payload, ct);

    public Task SendCtrlAltDelAsync(CancellationToken ct = default) =>
        SendAsync(DataChannelKind.Control, "ctrl_alt_del", new { }, ct);

    private async Task SendAsync(DataChannelKind kind, string type, object payload, CancellationToken ct)
    {
        var envelope = new DataChannelEnvelope
        {
            Type = type,
            Payload = JsonSerializer.SerializeToElement(payload, _jsonOptions)
        };
        var raw = JsonSerializer.Serialize(envelope, _jsonOptions);
        await _dataAgent.SendTextAsync(kind, raw, ct);
        // Suppress high-frequency messages to avoid log flooding
        if (type is not ("mouse_input" or "keyboard_input" or "ping" or "pong"))
            _onLog($"dc_sent:{kind}:{type}");
    }

    private void OnBinaryMessageReceived(DataChannelKind kind, byte[] data)
    {
        if (data.Length < BinaryHeaderSize) return;
        try
        {
            var sequence = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1, 4));
            var transferId = Encoding.ASCII.GetString(data, 5, 32).TrimEnd();
            var compressed = data[37] == 0x01;
            var chunkData = new byte[data.Length - BinaryHeaderSize];
            Buffer.BlockCopy(data, BinaryHeaderSize, chunkData, 0, chunkData.Length);
            BinaryFileChunkReceived?.Invoke(transferId, sequence, compressed, chunkData);
        }
        catch (Exception ex)
        {
            _onLog("dc_binary_parse_error:" + ex.Message);
        }
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
                case "file_ack":
                    FileAckReceived?.Invoke(envelope.Payload.Deserialize<FileAckPayload>(_jsonOptions) ?? new FileAckPayload());
                    break;
                case "file_error":
                    FileErrorReceived?.Invoke(envelope.Payload.Deserialize<FileErrorPayload>(_jsonOptions) ?? new FileErrorPayload());
                    break;
                case "dir_list_request":
                    DirListRequestReceived?.Invoke(envelope.Payload.Deserialize<DirListRequestPayload>(_jsonOptions) ?? new DirListRequestPayload());
                    break;
                case "dir_list_response":
                    DirListResponseReceived?.Invoke(envelope.Payload.Deserialize<DirListResponsePayload>(_jsonOptions) ?? new DirListResponsePayload());
                    break;
                case "file_request":
                    FileRequestReceived?.Invoke(envelope.Payload.Deserialize<FileRequestPayload>(_jsonOptions) ?? new FileRequestPayload());
                    break;
                case "create_folder_request":
                    CreateFolderRequestReceived?.Invoke(envelope.Payload.Deserialize<CreateFolderRequestPayload>(_jsonOptions) ?? new CreateFolderRequestPayload());
                    break;
                case "create_folder_response":
                    CreateFolderResponseReceived?.Invoke(envelope.Payload.Deserialize<CreateFolderResponsePayload>(_jsonOptions) ?? new CreateFolderResponsePayload());
                    break;
                case "delete_request":
                    DeleteRequestReceived?.Invoke(envelope.Payload.Deserialize<DeleteRequestPayload>(_jsonOptions) ?? new DeleteRequestPayload());
                    break;
                case "delete_response":
                    DeleteResponseReceived?.Invoke(envelope.Payload.Deserialize<DeleteResponsePayload>(_jsonOptions) ?? new DeleteResponsePayload());
                    break;
                case "ping":
                    PingReceived?.Invoke(envelope.Payload.Deserialize<PingPayload>(_jsonOptions) ?? new PingPayload());
                    break;
                case "pong":
                    PongReceived?.Invoke(envelope.Payload.Deserialize<PongPayload>(_jsonOptions) ?? new PongPayload());
                    break;
                case "ctrl_alt_del":
                    CtrlAltDelReceived?.Invoke();
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

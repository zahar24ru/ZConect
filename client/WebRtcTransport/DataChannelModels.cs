using System.Text.Json;

namespace WebRtcTransport;

public enum DataChannelKind
{
    Control,
    Input,
    Clipboard,
    File
}

public sealed class DataChannelEnvelope
{
    public string Type { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}

public sealed class ClipboardTextPayload
{
    public string Text { get; set; } = string.Empty;
    public string OriginPeerId { get; set; } = string.Empty;
}

public sealed class MouseInputPayload
{
    public string Action { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Button { get; set; }
    public int Delta { get; set; }
}

public sealed class KeyboardInputPayload
{
    public string Action { get; set; } = string.Empty;
    public int VirtualKey { get; set; }
    public bool Alt { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
}

public sealed class ScreenMetaPayload
{
    // Absolute capture rectangle in remote virtual screen coordinates (SetCursorPos compatible).
    public int CaptureX { get; set; }
    public int CaptureY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string DisplayId { get; set; } = string.Empty;
}

public sealed class HostVideoSettingsRequestPayload
{
    public string QualityPreset { get; set; } = "Auto";
    public string DisplayMode { get; set; } = "Current";
    public string DisplayId { get; set; } = string.Empty;
    public bool QuickReconnect { get; set; }
}

public sealed class CursorShapePayload
{
    public string CursorType { get; set; } = "arrow";
}

public sealed class DisplayInfoPayload
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
}

public sealed class HostDisplaysPayload
{
    public List<DisplayInfoPayload> Displays { get; set; } = new();
}

public sealed class HostDisplaysRequestPayload
{
    public string RequestId { get; set; } = string.Empty;
}

public sealed class FileMetaPayload
{
    public string TransferId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string MimeType { get; set; } = "application/octet-stream";
    public string Hash { get; set; } = string.Empty;
}

public sealed class FileChunkPayload
{
    public string TransferId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string Base64Data { get; set; } = string.Empty;
}

public sealed class FileEndPayload
{
    public string TransferId { get; set; } = string.Empty;
}

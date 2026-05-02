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
    public int ScanCode { get; set; }
    public bool Alt { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
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
    /// <summary>Папка на стороне получателя, куда сохранить файл. Если задана — получатель сохраняет сюда вместо папки приёма по умолчанию.</summary>
    public string? TargetDirectory { get; set; }
    /// <summary>Относительный путь внутри целевой папки (для передачи папок). Например "subdir\\file.txt".</summary>
    public string? RelativePath { get; set; }
    /// <summary>Если true — чанки сжаты GZip перед отправкой.</summary>
    public bool Compressed { get; set; }
    /// <summary>Total files in the batch (folder upload). 0 = unknown. Receiver uses for progress.</summary>
    public int BatchTotal { get; set; }
    /// <summary>Total bytes in the batch. 0 = unknown.</summary>
    public long BatchTotalBytes { get; set; }
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

public sealed class FileAckPayload
{
    public string TransferId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
}

public sealed class FileErrorPayload
{
    public string TransferId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Receiver → Sender: файл уже существует, спросить что делать. Sender отвечает
/// FileConflictResponsePayload. Флаг ApplyToAll=true в ответе — remember action для остальных
/// файлов в той же очереди (реализовано receiver-side через `_conflictActionForAll`).</summary>
public sealed class FileConflictQueryPayload
{
    public string TransferId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

/// <summary>Sender → Receiver: решение user'а на конфликт. Action: "rename" | "overwrite" | "skip".</summary>
public sealed class FileConflictResponsePayload
{
    public string TransferId { get; set; } = string.Empty;
    public string Action { get; set; } = "rename";
    public bool ApplyToAll { get; set; }
}

/// <summary>Запрос списка каталога на удалённой стороне (для правой панели FT).</summary>
public sealed class DirListRequestPayload
{
    public string Path { get; set; } = string.Empty;
    /// <summary>Идентификатор запроса; ответ должен содержать тот же Id.</summary>
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>Элемент в списке каталога удалённой стороны.</summary>
public sealed class RemoteFileItemPayload
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long DateModifiedMs { get; set; } // Unix milliseconds
}

/// <summary>Запрос переименования файла/папки на удалённой стороне.</summary>
public sealed class RenameRequestPayload
{
    public string Path { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>Ответ на запрос переименования.</summary>
public sealed class RenameResponsePayload
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>Ответ со списком каталога удалённой стороны.</summary>
public sealed class DirListResponsePayload
{
    public string Path { get; set; } = string.Empty;
    public List<RemoteFileItemPayload> Items { get; set; } = new();
    /// <summary>Идентификатор запроса, на который отвечаем.</summary>
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>Запрос файла с удалённой стороны (скачать с Host).</summary>
public sealed class FileRequestPayload
{
    public string Path { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>Запрос скачивания папки (рекурсивно) с удалённой стороны.</summary>
public sealed class FolderDownloadRequestPayload
{
    public string Path { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>Ответ: список файлов для скачивания папки (host → viewer).</summary>
public sealed class FolderDownloadResponsePayload
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    /// <summary>Количество файлов, которые host начнёт отправлять.</summary>
    public int FileCount { get; set; }
}

/// <summary>Запрос создания папки на удалённой стороне (Host создаёт папку).</summary>
public sealed class CreateFolderRequestPayload
{
    public string ParentPath { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>Ответ на запрос создания папки.</summary>
public sealed class CreateFolderResponsePayload
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>Запрос удаления файла или папки на удалённой стороне.</summary>
public sealed class DeleteRequestPayload
{
    public string Path { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>Ответ на запрос удаления.</summary>
public sealed class DeleteResponsePayload
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>Ping payload for RTT measurement. Sender records timestamp, receiver echoes it back as pong.</summary>
public sealed class PingPayload
{
    public long TimestampMs { get; set; }
}

/// <summary>Pong response — echoes sender's timestamp for RTT calculation.</summary>
public sealed class PongPayload
{
    public long TimestampMs { get; set; }
}

/// <summary>Viewer → Host: запрос запустить whitelisted процесс (сейчас только "taskmgr").
/// Host whitelist enforced двумя слоями: MainViewModel (UI trust boundary) + ProcessLauncher
/// (service trust boundary). Unknown names silently dropped с warning log.</summary>
public sealed class LaunchProcessPayload
{
    public string Name { get; set; } = string.Empty;
}

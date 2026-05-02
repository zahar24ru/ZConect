using WebRtcTransport;

namespace FileTransfer;

/// <summary>
/// Канал передачи файлов поверх dc-file (тот же коннект, что и удалённое управление).
/// Реализацией является адаптер к <see cref="DataChannelCoordinator"/>.
/// </summary>
public interface IFileTransferChannel
{
    Task SendFileMetaAsync(FileMetaPayload payload, CancellationToken ct = default);
    Task SendFileChunkAsync(FileChunkPayload payload, CancellationToken ct = default);
    /// <summary>Send binary file chunk (no JSON/Base64 overhead).</summary>
    Task SendBinaryFileChunkAsync(string transferId, int sequence, byte[] data, bool compressed, CancellationToken ct = default);
    Task SendFileEndAsync(FileEndPayload payload, CancellationToken ct = default);
    Task SendFileAckAsync(FileAckPayload payload, CancellationToken ct = default);
    Task SendFileErrorAsync(FileErrorPayload payload, CancellationToken ct = default);
    Task SendFileConflictQueryAsync(FileConflictQueryPayload payload, CancellationToken ct = default);
    Task SendFileConflictResponseAsync(FileConflictResponsePayload payload, CancellationToken ct = default);

    event Action<FileMetaPayload>? FileMetaReceived;
    event Action<FileChunkPayload>? FileChunkReceived;
    /// <summary>Binary chunk received (transferId, sequence, compressed, rawData).</summary>
    event Action<string, int, bool, byte[]>? BinaryFileChunkReceived;
    event Action<FileEndPayload>? FileEndReceived;
    event Action<FileAckPayload>? FileAckReceived;
    event Action<FileErrorPayload>? FileErrorReceived;
    event Action<FileConflictQueryPayload>? FileConflictQueryReceived;
    event Action<FileConflictResponsePayload>? FileConflictResponseReceived;
}

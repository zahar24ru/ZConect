using WebRtcTransport;

namespace FileTransfer;

/// <summary>
/// Адаптер: подключает модуль FileTransfer к тому же dc-file, что и удалённое управление.
/// </summary>
public sealed class DataChannelFileTransferChannel : IFileTransferChannel
{
    private readonly DataChannelCoordinator _dc;

    public DataChannelFileTransferChannel(DataChannelCoordinator dc)
    {
        _dc = dc;
        _dc.FileMetaReceived += (p) => FileMetaReceived?.Invoke(p);
        _dc.FileChunkReceived += (p) => FileChunkReceived?.Invoke(p);
        _dc.BinaryFileChunkReceived += (tid, seq, compressed, data) => BinaryFileChunkReceived?.Invoke(tid, seq, compressed, data);
        _dc.FileEndReceived += (p) => FileEndReceived?.Invoke(p);
        _dc.FileAckReceived += (p) => FileAckReceived?.Invoke(p);
        _dc.FileErrorReceived += (p) => FileErrorReceived?.Invoke(p);
    }

    public Task SendFileMetaAsync(FileMetaPayload payload, CancellationToken ct = default) =>
        _dc.SendFileMetaAsync(payload, ct);

    public Task SendFileChunkAsync(FileChunkPayload payload, CancellationToken ct = default) =>
        _dc.SendFileChunkAsync(payload, ct);

    public Task SendBinaryFileChunkAsync(string transferId, int sequence, byte[] data, bool compressed, CancellationToken ct = default) =>
        _dc.SendBinaryFileChunkAsync(transferId, sequence, data, compressed, ct);

    public Task SendFileEndAsync(FileEndPayload payload, CancellationToken ct = default) =>
        _dc.SendFileEndAsync(payload, ct);

    public Task SendFileAckAsync(FileAckPayload payload, CancellationToken ct = default) =>
        _dc.SendFileAckAsync(payload, ct);

    public Task SendFileErrorAsync(FileErrorPayload payload, CancellationToken ct = default) =>
        _dc.SendFileErrorAsync(payload, ct);

    public event Action<FileMetaPayload>? FileMetaReceived;
    public event Action<FileChunkPayload>? FileChunkReceived;
    public event Action<string, int, bool, byte[]>? BinaryFileChunkReceived;
    public event Action<FileEndPayload>? FileEndReceived;
    public event Action<FileAckPayload>? FileAckReceived;
    public event Action<FileErrorPayload>? FileErrorReceived;
}

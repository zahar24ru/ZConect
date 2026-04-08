using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using WebRtcTransport;

namespace FileTransfer;

public sealed class FileTransferService
{
    public const int DefaultChunkSizeBytes = 64 * 1024;

    private readonly IFileTransferChannel _channel;
    private readonly int _chunkSizeBytes;
    private readonly Func<string> _getIncomingTempDir;
    private readonly Func<string> _getIncomingSaveDir;
    private readonly Func<string>? _getOutgoingTargetDir;
    private readonly Action<string, string, object[]>? _onLog;

    private readonly ConcurrentDictionary<string, OutgoingState> _outgoing = new();
    private readonly ConcurrentDictionary<string, IncomingState> _incoming = new();
    private readonly Queue<(string Path, string? RelativePath)> _sendQueue = new();
    private readonly object _queueLock = new();
    private string? _currentSendTransferId;

    public event Action<TransferItem>? TransferProgress;
    public event Action<TransferItem>? TransferCompleted;
    public event Action<TransferItem>? TransferFailed;

    public FileTransferService(
        IFileTransferChannel channel,
        int chunkSizeBytes = DefaultChunkSizeBytes,
        Func<string>? getIncomingTempDir = null,
        Func<string>? getIncomingSaveDir = null,
        Func<string>? getOutgoingTargetDir = null,
        Action<string, string, object[]>? onLog = null)
    {
        _channel = channel;
        _chunkSizeBytes = chunkSizeBytes;
        _getIncomingTempDir = getIncomingTempDir ?? GetDefaultTempDir;
        _getIncomingSaveDir = getIncomingSaveDir ?? GetDefaultSaveDir;
        _getOutgoingTargetDir = getOutgoingTargetDir;
        _onLog = onLog;

        _channel.FileMetaReceived += OnFileMetaReceived;
        _channel.FileChunkReceived += OnFileChunkReceived;
        _channel.BinaryFileChunkReceived += OnBinaryFileChunkReceived;
        _channel.FileEndReceived += OnFileEndReceived;
        _channel.FileAckReceived += OnFileAckReceived;
        _channel.FileErrorReceived += OnFileErrorReceived;
    }

    private static string GetDefaultTempDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZConect", "IncomingTemp");

    private static string GetDefaultSaveDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ZConectReceived");

    /// <summary>Добавить файл в очередь отправки. relativePath — для передачи папок (путь внутри папки на получателе). Ограничений по размеру файла нет.</summary>
    public void EnqueueSend(string filePath, string? relativePath = null)
    {
        if (!File.Exists(filePath))
        {
            _onLog?.Invoke("FileTransfer", "enqueue_file_not_found", new object[] { filePath });
            return;
        }

        lock (_queueLock)
        {
            _sendQueue.Enqueue((filePath, relativePath));
        }

        _onLog?.Invoke("FileTransfer", "enqueued", new object[] { Path.GetFileName(filePath) });
        _ = ProcessQueueAsync();
    }

    /// <summary>Добавить папку в очередь: рекурсивно все файлы с относительными путями.</summary>
    public void EnqueueSendFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            _onLog?.Invoke("FileTransfer", "enqueue_folder_not_found", new object[] { folderPath });
            return;
        }
        var baseDir = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(baseDir);
        // parentDir = one level above the folder being sent, so relativePath includes the folder name itself.
        var parentDir = Path.GetDirectoryName(baseDir) ?? baseDir;
        var count = 0;
        lock (_queueLock)
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fullPath = Path.GetFullPath(file);
                    // Include the folder name: "USB\subfolder\file.txt" instead of "subfolder\file.txt"
                    var relativePath = fullPath.Substring(parentDir.Length + 1);
                    if (Path.DirectorySeparatorChar != '\\')
                        relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '\\');
                    _sendQueue.Enqueue((fullPath, relativePath));
                    count++;
                }
                catch { /* skip inaccessible */ }
            }
        }
        _onLog?.Invoke("FileTransfer", "enqueued_folder", new object[] { Path.GetFileName(folderPath), count });
        if (count > 0)
            _ = ProcessQueueAsync();
    }

    /// <summary>Приостановить отправку по transferId.</summary>
    public void Pause(string transferId)
    {
        if (_outgoing.TryGetValue(transferId, out var state))
        {
            state.Paused = true;
            RaiseProgress(state, TransferStatus.Paused);
        }
    }

    /// <summary>Продолжить отправку.</summary>
    public void Resume(string transferId)
    {
        if (_outgoing.TryGetValue(transferId, out var state))
        {
            state.Paused = false;
            RaiseProgress(state, TransferStatus.InProgress);
        }
    }

    /// <summary>Отменить все передачи (при отключении сессии).</summary>
    public void CancelAll()
    {
        foreach (var id in _outgoing.Keys.ToArray())
            Cancel(id);
        foreach (var id in _incoming.Keys.ToArray())
            Cancel(id);
    }

    /// <summary>Отменить передачу.</summary>
    public void Cancel(string transferId)
    {
        if (_outgoing.TryGetValue(transferId, out var state))
            state.Cancelled = true;
        if (_incoming.TryGetValue(transferId, out var inc))
        {
            AbortIncoming(transferId, inc, deletePartial: true);
        }
    }

    private async Task ProcessQueueAsync()
    {
        (string Path, string? RelativePath) item;
        lock (_queueLock)
        {
            if (_currentSendTransferId != null)
                return;
            if (_sendQueue.Count == 0)
                return;
            item = _sendQueue.Dequeue();
        }

        try
        {
            await SendOneFileAsync(item.Path, item.RelativePath).ConfigureAwait(false);
        }
        finally
        {
            lock (_queueLock)
            {
                _currentSendTransferId = null;
                if (_sendQueue.Count > 0)
                    _ = ProcessQueueAsync();
            }
        }
    }

    private async Task SendOneFileAsync(string filePath, string? relativePath = null)
    {
        var transferId = Guid.NewGuid().ToString("N");
        var fileInfo = new FileInfo(filePath);
        var fileName = fileInfo.Name;
        var state = new OutgoingState(transferId, fileName, filePath, fileInfo.Length);
        _outgoing[transferId] = state;

        try
        {
            RaiseProgress(state, TransferStatus.InProgress);
            _currentSendTransferId = transferId;

            var hash = await ComputeSha256Async(filePath, state.Cts.Token).ConfigureAwait(false);
            var targetDir = _getOutgoingTargetDir?.Invoke()?.Trim();
            var useCompression = ShouldCompress(fileName);
            var payload = new FileMetaPayload
            {
                TransferId = transferId,
                FileName = fileName,
                FileSize = fileInfo.Length,
                MimeType = "application/octet-stream",
                Hash = hash,
                TargetDirectory = string.IsNullOrEmpty(targetDir) ? null : targetDir,
                RelativePath = string.IsNullOrEmpty(relativePath) ? null : relativePath,
                Compressed = useCompression
            };
            await _channel.SendFileMetaAsync(payload, state.Cts.Token).ConfigureAwait(false);

            var sequence = 0;
            var buffer = new byte[_chunkSizeBytes];
            await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                while (true)
                {
                    if (state.Cancelled)
                    {
                        await SendErrorAsync(transferId, "cancelled", "Отменено пользователем").ConfigureAwait(false);
                        RaiseFailed(state, "Отменено");
                        return;
                    }

                    while (state.Paused && !state.Cancelled)
                        await Task.Delay(50, state.Cts.Token).ConfigureAwait(false);

                    if (state.Cancelled) break;

                    var read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), state.Cts.Token).ConfigureAwait(false);
                    if (read <= 0) break;

                    var chunkData = buffer.AsSpan(0, read).ToArray();
                    if (useCompression)
                        chunkData = CompressGZip(chunkData);

                    await _channel.SendBinaryFileChunkAsync(transferId, sequence++, chunkData, useCompression, state.Cts.Token).ConfigureAwait(false);

                    state.SentBytes += read;
                    RaiseProgress(state, TransferStatus.InProgress);

                    if ((sequence & 0xF) == 0)
                        await Task.Delay(1, state.Cts.Token).ConfigureAwait(false);
                }
            }

            await _channel.SendFileEndAsync(new FileEndPayload { TransferId = transferId }, state.Cts.Token).ConfigureAwait(false);
            state.SentBytes = state.TotalBytes;
            RaiseProgress(state, TransferStatus.AwaitingAck);

            // Wait for receiver ACK (hash verified, file moved) with timeout.
            using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(state.Cts.Token);
            ackCts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var ackSuccess = await state.AckTcs.Task.WaitAsync(ackCts.Token).ConfigureAwait(false);
                if (ackSuccess)
                {
                    RaiseProgress(state, TransferStatus.Completed);
                    RaiseCompleted(ToTransferItem(state));
                    _onLog?.Invoke("FileTransfer", "file_sent", new object[] { fileName, fileInfo.Length });
                }
                else
                {
                    RaiseFailed(state, "Получатель отклонил файл");
                }
            }
            catch (OperationCanceledException) when (!state.Cts.IsCancellationRequested)
            {
                // ACK timeout — mark complete anyway (receiver may have succeeded but ACK was lost).
                RaiseProgress(state, TransferStatus.Completed);
                RaiseCompleted(ToTransferItem(state));
                _onLog?.Invoke("FileTransfer", "file_sent_ack_timeout", new object[] { fileName, fileInfo.Length });
            }
        }
        catch (OperationCanceledException)
        {
            if (!state.Cancelled)
                RaiseFailed(state, "Прервано");
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("FileTransfer", "file_send_failed", new object[] { ex.Message });
            await SendErrorAsync(transferId, "send_error", ex.Message).ConfigureAwait(false);
            RaiseFailed(state, ex.Message);
        }
        finally
        {
            _outgoing.TryRemove(transferId, out _);
            state.Cts.Dispose();
        }
    }

    private async Task SendErrorAsync(string transferId, string code, string message)
    {
        try
        {
            await _channel.SendFileErrorAsync(new FileErrorPayload
            {
                TransferId = transferId,
                Code = code,
                Message = message
            }).ConfigureAwait(false);
        }
        catch { /* best effort */ }
    }

    private void OnFileMetaReceived(FileMetaPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransferId)) return;

        var safeName = Path.GetFileName(payload.FileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "received-" + payload.TransferId + ".bin";

        try
        {
            var tempDir = _getIncomingTempDir();
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, payload.TransferId + ".part");
            var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var targetDir = payload.TargetDirectory?.Trim();
            var relativePath = payload.RelativePath?.Trim();
            var state = new IncomingState(payload.TransferId, safeName, tempPath, payload.Hash ?? string.Empty, payload.FileSize, stream, string.IsNullOrEmpty(targetDir) ? null : targetDir, string.IsNullOrEmpty(relativePath) ? null : relativePath);

            if (_incoming.TryRemove(payload.TransferId, out var old))
                old.Dispose();
            _incoming[payload.TransferId] = state;

            var item = new TransferItem
            {
                TransferId = state.TransferId,
                FileName = state.FileName,
                Direction = TransferDirection.Incoming,
                Status = TransferStatus.InProgress,
                TotalBytes = state.ExpectedSize,
                CurrentBytes = 0
            };
            RaiseProgress(item);
            _onLog?.Invoke("FileTransfer", "file_meta_received", new object[] { safeName, payload.FileSize });
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("FileTransfer", "file_meta_receive_failed", new object[] { ex.Message });
            _ = _channel.SendFileErrorAsync(new FileErrorPayload
            {
                TransferId = payload.TransferId,
                Code = "receive_error",
                Message = ex.Message
            });
        }
    }

    private void OnFileChunkReceived(FileChunkPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransferId)) return;

        if (!_incoming.TryGetValue(payload.TransferId, out var state))
        {
            _onLog?.Invoke("FileTransfer", "file_chunk_without_meta", new object[] { payload.TransferId });
            return;
        }

        try
        {
            if (payload.Sequence != state.NextSequence)
            {
                _onLog?.Invoke("FileTransfer", "file_chunk_sequence_mismatch", new object[] { payload.TransferId });
                AbortIncoming(payload.TransferId, state, true);
                return;
            }

            var chunkBytes = Convert.FromBase64String(payload.Base64Data ?? string.Empty);
            lock (state.Sync)
            {
                state.Stream.Write(chunkBytes, 0, chunkBytes.Length);
                state.NextSequence++;
                state.BytesWritten += chunkBytes.Length;
            }

            var item = new TransferItem
            {
                TransferId = state.TransferId,
                FileName = state.FileName,
                Direction = TransferDirection.Incoming,
                Status = TransferStatus.InProgress,
                TotalBytes = state.ExpectedSize,
                CurrentBytes = state.BytesWritten
            };
            RaiseProgress(item);
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("FileTransfer", "file_chunk_receive_failed", new object[] { ex.Message });
            AbortIncoming(payload.TransferId, state, true);
            _ = _channel.SendFileErrorAsync(new FileErrorPayload { TransferId = payload.TransferId, Code = "chunk_error", Message = ex.Message });
        }
    }

    private void OnBinaryFileChunkReceived(string transferId, int sequence, bool compressed, byte[] rawData)
    {
        if (string.IsNullOrWhiteSpace(transferId)) return;

        if (!_incoming.TryGetValue(transferId, out var state))
        {
            _onLog?.Invoke("FileTransfer", "binary_chunk_without_meta", new object[] { transferId });
            return;
        }

        try
        {
            if (sequence != state.NextSequence)
            {
                _onLog?.Invoke("FileTransfer", "binary_chunk_sequence_mismatch", new object[] { transferId });
                AbortIncoming(transferId, state, true);
                return;
            }

            var chunkBytes = compressed ? DecompressGZip(rawData) : rawData;
            lock (state.Sync)
            {
                state.Stream.Write(chunkBytes, 0, chunkBytes.Length);
                state.NextSequence++;
                state.BytesWritten += chunkBytes.Length;
            }

            var item = new TransferItem
            {
                TransferId = state.TransferId,
                FileName = state.FileName,
                Direction = TransferDirection.Incoming,
                Status = TransferStatus.InProgress,
                TotalBytes = state.ExpectedSize,
                CurrentBytes = state.BytesWritten
            };
            RaiseProgress(item);
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("FileTransfer", "binary_chunk_receive_failed", new object[] { ex.Message });
            AbortIncoming(transferId, state, true);
            _ = _channel.SendFileErrorAsync(new FileErrorPayload { TransferId = transferId, Code = "chunk_error", Message = ex.Message });
        }
    }

    private void OnFileEndReceived(FileEndPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransferId)) return;

        if (!_incoming.TryRemove(payload.TransferId, out var state))
        {
            _onLog?.Invoke("FileTransfer", "file_end_without_transfer", new object[] { payload.TransferId });
            return;
        }

        try
        {
            lock (state.Sync)
            {
                state.Stream.Flush();
            }
            state.Dispose();

            if (!string.IsNullOrWhiteSpace(state.ExpectedHash))
            {
                var actualHash = ComputeSha256(state.TargetPath);
                if (!string.Equals(actualHash, state.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _onLog?.Invoke("FileTransfer", "file_hash_mismatch", new object[] { state.FileName });
                    _ = _channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = false, Message = "Hash mismatch" });
                    RaiseFailed(new TransferItem
                    {
                        TransferId = state.TransferId,
                        FileName = state.FileName,
                        Direction = TransferDirection.Incoming,
                        Status = TransferStatus.Failed,
                        TotalBytes = state.ExpectedSize,
                        CurrentBytes = state.BytesWritten,
                        ErrorMessage = "Hash не совпал"
                    });
                    return;
                }
            }

            var saveDir = !string.IsNullOrEmpty(state.TargetDirectory) ? state.TargetDirectory : _getIncomingSaveDir();
            _onLog?.Invoke("FileTransfer", "incoming_save_dir_used", new object[] { "path", saveDir });
            string finalPath;
            if (!string.IsNullOrEmpty(state.RelativePath))
            {
                finalPath = Path.Combine(saveDir, state.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(finalPath))
                    finalPath = GetUniqueFilePath(Path.GetDirectoryName(finalPath) ?? saveDir, Path.GetFileName(finalPath));
            }
            else
            {
                Directory.CreateDirectory(saveDir);
                finalPath = GetUniqueFilePath(saveDir, state.FileName);
            }
            File.Move(state.TargetPath, finalPath, overwrite: true);

            _ = _channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = true });
            _onLog?.Invoke("FileTransfer", "file_received_ok", new object[] { state.FileName, state.BytesWritten, "saved_to", saveDir });

            var total = state.ExpectedSize > 0 ? state.ExpectedSize : state.BytesWritten;
            RaiseCompleted(new TransferItem
            {
                TransferId = state.TransferId,
                FileName = state.FileName,
                Direction = TransferDirection.Incoming,
                Status = TransferStatus.Completed,
                TotalBytes = total,
                CurrentBytes = total
            });
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("FileTransfer", "file_end_receive_failed", new object[] { ex.Message });
            _ = _channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = false, Message = ex.Message });
            RaiseFailed(new TransferItem
            {
                TransferId = state.TransferId,
                FileName = state.FileName,
                Direction = TransferDirection.Incoming,
                Status = TransferStatus.Failed,
                TotalBytes = state.ExpectedSize,
                CurrentBytes = state.BytesWritten,
                ErrorMessage = ex.Message
            });
        }
    }

    private void OnFileAckReceived(FileAckPayload payload)
    {
        if (payload is null) return;
        if (_outgoing.TryGetValue(payload.TransferId, out var state))
        {
            state.AckTcs.TrySetResult(payload.Success);
            if (!payload.Success)
                RaiseFailed(state, payload.Message ?? "Ошибка на стороне получателя");
        }
    }

    private void OnFileErrorReceived(FileErrorPayload payload)
    {
        if (payload is null) return;
        if (_outgoing.TryGetValue(payload.TransferId, out var state))
            RaiseFailed(state, payload.Message);
    }

    private void AbortIncoming(string transferId, IncomingState state, bool deletePartial)
    {
        _incoming.TryRemove(transferId, out _);
        try
        {
            state.Dispose();
            if (deletePartial && File.Exists(state.TargetPath))
                File.Delete(state.TargetPath);
        }
        catch { /* ignore */ }
    }

    private static TransferItem ToTransferItem(OutgoingState state) =>
        new()
        {
            TransferId = state.TransferId,
            FileName = state.FileName,
            Direction = TransferDirection.Outgoing,
            Status = state.Status,
            TotalBytes = state.TotalBytes,
            CurrentBytes = state.SentBytes
        };

    private void RaiseProgress(OutgoingState state, TransferStatus status)
    {
        state.Status = status;
        RaiseProgress(ToTransferItem(state));
    }

    private void RaiseProgress(TransferItem item) => TransferProgress?.Invoke(item);
    private void RaiseCompleted(TransferItem item) => TransferCompleted?.Invoke(item);
    private void RaiseFailed(OutgoingState state, string message)
    {
        RaiseFailed(new TransferItem
        {
            TransferId = state.TransferId,
            FileName = state.FileName,
            Direction = TransferDirection.Outgoing,
            Status = TransferStatus.Failed,
            TotalBytes = state.TotalBytes,
            CurrentBytes = state.SentBytes,
            ErrorMessage = message
        });
    }
    private void RaiseFailed(TransferItem item) => TransferFailed?.Invoke(item);

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string ComputeSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }

    private static string GetUniqueFilePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 10000; i++)
        {
            path = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, stem + "_" + Guid.NewGuid().ToString("N")[..8] + ext);
    }

    private static byte[] CompressGZip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] DecompressGZip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Whether to use GZip compression for this file type (skip already-compressed formats).</summary>
    private static bool ShouldCompress(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            // Already compressed — skip.
            ".zip" or ".rar" or ".7z" or ".gz" or ".bz2" or ".xz" or ".zst" or ".cab"
                or ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp"
                or ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm"
                or ".mp3" or ".aac" or ".ogg" or ".flac" or ".wma" or ".m4a"
                or ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" => false,
            // Everything else benefits from compression.
            _ => true,
        };
    }

    private sealed class OutgoingState
    {
        public readonly string TransferId;
        public readonly string FileName;
        public readonly string FilePath;
        public readonly long TotalBytes;
        public long SentBytes;
        public TransferStatus Status;
        public bool Paused;
        public bool Cancelled;
        public readonly CancellationTokenSource Cts = new();
        public readonly TaskCompletionSource<bool> AckTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OutgoingState(string transferId, string fileName, string filePath, long totalBytes)
        {
            TransferId = transferId;
            FileName = fileName;
            FilePath = filePath;
            TotalBytes = totalBytes;
        }
    }

    private sealed class IncomingState : IDisposable
    {
        public readonly string TransferId;
        public readonly string FileName;
        public readonly string TargetPath;
        public readonly string ExpectedHash;
        public readonly long ExpectedSize;
        public readonly FileStream Stream;
        public readonly object Sync = new();
        public readonly string? TargetDirectory;
        public readonly string? RelativePath;
        public int NextSequence;
        public long BytesWritten;

        public IncomingState(string transferId, string fileName, string targetPath, string expectedHash, long expectedSize, FileStream stream, string? targetDirectory = null, string? relativePath = null)
        {
            TransferId = transferId;
            FileName = fileName;
            TargetPath = targetPath;
            ExpectedHash = expectedHash;
            ExpectedSize = expectedSize;
            Stream = stream;
            TargetDirectory = targetDirectory;
            RelativePath = relativePath;
        }

        public void Dispose() => Stream.Dispose();
    }
}

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using WebRtcTransport;

namespace FileTransfer;

/// <summary>User's choice when a file already exists at the destination.</summary>
public enum ConflictAction { Overwrite, Rename, Skip, OverwriteAll, SkipAll }

public sealed class FileTransferService
{
    public const int DefaultChunkSizeBytes = 64 * 1024;

    private readonly IFileTransferChannel _channel;
    private readonly int _chunkSizeBytes;
    private readonly Func<string> _getIncomingTempDir;
    private readonly Func<string> _getIncomingSaveDir;
    private readonly Func<string>? _getOutgoingTargetDir;
    private readonly Action<string, string, object[]>? _onLog;

    /// <summary>LEGACY local fallback — used by tests with mock channel (FT #3 2026-04-19 remote
    /// flow moved dialog на sender side). Production wiring НЕ устанавливает OnFileConflict —
    /// receiver шлёт FileConflictQuery на sender, тот показывает dialog, шлёт Response. Если этот
    /// callback set — receiver использует его вместо remote query.</summary>
    public Func<string, ConflictAction>? OnFileConflict { get; set; }
    private ConflictAction? _conflictActionForAll; // OverwriteAll/SkipAll remembered

    /// <summary>Fires on SENDER side when receiver reports file conflict. Handler должен показать
    /// dialog и отправить response через SendConflictResponseAsync. До 30 сек — потом receiver
    /// fallback'нет на Rename. Background thread — UI должен Dispatcher.Invoke.</summary>
    public event Action<FileConflictQueryPayload>? FileConflictQueryReceived;

    /// <summary>Receiver-side: pending queries awaiting sender response.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FileConflictResponsePayload>> _pendingConflictQueries = new();

    /// <summary>Serializes conflict resolution in OnFileEndReceived so "Apply to all" (first file
    /// sets _conflictActionForAll) действительно применяется к следующим файлам в batch'e.
    /// Без этого lock'а mrwebrtc может dispatch'ить 3 file_end events параллельно на ThreadPool
    /// → все три видят _conflictActionForAll=null одновременно → все шлют queries (observed в
    /// FT_conflict_overwrite_all test fail 2026-04-19).</summary>
    private readonly object _conflictResolutionLock = new();

    private readonly ConcurrentDictionary<string, OutgoingState> _outgoing = new();
    private readonly ConcurrentDictionary<string, IncomingState> _incoming = new();
    private readonly Queue<(string Path, string? RelativePath)> _sendQueue = new();
    private readonly object _queueLock = new();
    private string? _currentSendTransferId;
    private volatile bool _queuePaused; // queue-level pause: prevents dequeuing next file
    private int _batchTotal;       // total files in current batch (for receiver progress)
    private long _batchTotalBytes;  // total bytes in current batch

    /// <summary>Set batch info for the next group of enqueued files (for receiver progress display).</summary>
    public void SetBatchInfo(int totalFiles, long totalBytes)
    {
        _batchTotal = totalFiles;
        _batchTotalBytes = totalBytes;
    }

    public event Action<TransferItem>? TransferProgress;
    public event Action<TransferItem>? TransferCompleted;
    public event Action<TransferItem>? TransferFailed;
    /// <summary>Fired when outgoing queue changes. Args: (filesAdded, totalBytesAdded).</summary>
    public event Action<int, long>? QueueChanged;

    /// <summary>True если local machine is the "Viewer" role — user непосредственно здесь
    /// нажимает кнопки в FT (initiator). Определяет куда routeить dialog conflict:
    ///   • canShowLocalDialogForIncoming=true (я Viewer) → local OnFileConflict на receiver-end;
    ///   • canShowLocalDialogForIncoming=false (я Host) → remote query к Viewer sender'у.
    /// При двунаправленном FT: sender всегда wire FileConflictQueryReceived + receiver wire
    /// OnFileConflict — но только ОДИН из них активен per transfer в зависимости от role.</summary>
    private readonly bool _canShowLocalDialogForIncoming;

    /// <summary>True: OnFileEndReceived offloads to ThreadPool (production — избегает DC thread
    /// deadlock на .Wait inside ResolveConflictViaRemote). False: sync execution для mock-based
    /// unit tests которые polagaются на immediate completion.</summary>
    private readonly bool _offloadFileEndToThreadPool;

    public FileTransferService(
        IFileTransferChannel channel,
        int chunkSizeBytes = DefaultChunkSizeBytes,
        Func<string>? getIncomingTempDir = null,
        Func<string>? getIncomingSaveDir = null,
        Func<string>? getOutgoingTargetDir = null,
        Action<string, string, object[]>? onLog = null,
        bool canShowLocalDialogForIncoming = true,
        bool offloadFileEndToThreadPool = true)
    {
        _channel = channel;
        _chunkSizeBytes = chunkSizeBytes;
        _getIncomingTempDir = getIncomingTempDir ?? GetDefaultTempDir;
        _getIncomingSaveDir = getIncomingSaveDir ?? GetDefaultSaveDir;
        _getOutgoingTargetDir = getOutgoingTargetDir;
        _onLog = onLog;
        _canShowLocalDialogForIncoming = canShowLocalDialogForIncoming;
        _offloadFileEndToThreadPool = offloadFileEndToThreadPool;

        _channel.FileMetaReceived += OnFileMetaReceived;
        _channel.FileChunkReceived += OnFileChunkReceived;
        _channel.BinaryFileChunkReceived += OnBinaryFileChunkReceived;
        _channel.FileEndReceived += OnFileEndReceived;
        _channel.FileAckReceived += OnFileAckReceived;
        _channel.FileErrorReceived += OnFileErrorReceived;
        _channel.FileConflictQueryReceived += p => FileConflictQueryReceived?.Invoke(p);
        _channel.FileConflictResponseReceived += OnFileConflictResponseReceived;
    }

    /// <summary>Sender вызывает после показа dialog'а, чтобы доставить решение user'а receiver'у.</summary>
    public Task SendConflictResponseAsync(string transferId, ConflictAction action, bool applyToAll, CancellationToken ct = default)
    {
        var actionStr = action switch
        {
            ConflictAction.Overwrite or ConflictAction.OverwriteAll => "overwrite",
            ConflictAction.Skip or ConflictAction.SkipAll => "skip",
            _ => "rename"
        };
        var applyAll = applyToAll
            || action is ConflictAction.OverwriteAll or ConflictAction.SkipAll;
        return _channel.SendFileConflictResponseAsync(new FileConflictResponsePayload
        {
            TransferId = transferId,
            Action = actionStr,
            ApplyToAll = applyAll,
        }, ct);
    }

    private void OnFileConflictResponseReceived(FileConflictResponsePayload payload)
    {
        if (payload is null || string.IsNullOrEmpty(payload.TransferId)) return;
        if (_pendingConflictQueries.TryRemove(payload.TransferId, out var tcs))
            tcs.TrySetResult(payload);
    }

    /// <summary>Fire-and-forget wrapper: ловит exception при отправке (например DC в состоянии
    /// Connecting во время teardown session) и логирует, а не роняет finalizer thread через
    /// UnobservedTaskException (observed 2026-04-19 host logs). Используется для send'ов,
    /// которые нельзя await (внутри synchronous handler'ов).</summary>
    private void FireAndForget(Task task, string context)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var msg = t.Exception?.InnerException?.Message ?? t.Exception?.Message ?? "unknown";
                _onLog?.Invoke("FileTransfer", "fire_and_forget_failed", new object[] { context, msg });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Receiver blocks here until sender responds (or 30s timeout). Called from
    /// OnFileEndReceived, runs on DC thread — block is acceptable because SCTP already delivered
    /// the whole file and no further chunks are pending for this transfer.</summary>
    private ConflictAction ResolveConflictViaRemote(string transferId, string fileName)
    {
        var tcs = new TaskCompletionSource<FileConflictResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingConflictQueries.TryAdd(transferId, tcs))
        {
            _onLog?.Invoke("FileTransfer", "conflict_query_duplicate_id", new object[] { transferId });
            return ConflictAction.Rename;
        }

        try
        {
            FireAndForget(_channel.SendFileConflictQueryAsync(new FileConflictQueryPayload
            {
                TransferId = transferId,
                FileName = fileName,
            }), "file_conflict_query");
            _onLog?.Invoke("FileTransfer", "conflict_query_sent", new object[] { fileName });

            if (!tcs.Task.Wait(TimeSpan.FromSeconds(30)))
            {
                _onLog?.Invoke("FileTransfer", "conflict_query_timeout_fallback_rename", new object[] { fileName });
                return ConflictAction.Rename;
            }

            var payload = tcs.Task.Result;
            _onLog?.Invoke("FileTransfer", "conflict_response_received", new object[] { fileName, payload.Action, "apply_all", payload.ApplyToAll });

            var baseAction = payload.Action?.ToLowerInvariant() switch
            {
                "overwrite" => ConflictAction.Overwrite,
                "skip" => ConflictAction.Skip,
                _ => ConflictAction.Rename,
            };
            if (!payload.ApplyToAll) return baseAction;
            return baseAction switch
            {
                ConflictAction.Overwrite => ConflictAction.OverwriteAll,
                ConflictAction.Skip => ConflictAction.SkipAll,
                _ => ConflictAction.Rename, // Rename has no "all" — каждый файл просто rename'ится уникально.
            };
        }
        finally
        {
            _pendingConflictQueries.TryRemove(transferId, out _);
        }
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

        long fileSize = 0;
        try { fileSize = new FileInfo(filePath).Length; } catch { }

        lock (_queueLock)
        {
            _sendQueue.Enqueue((filePath, relativePath));
        }

        QueueChanged?.Invoke(1, fileSize);
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
        long totalBytes = 0;
        lock (_queueLock)
        {
            foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fullPath = Path.GetFullPath(file);
                    var relativePath = Path.GetRelativePath(parentDir, fullPath);
                    if (Path.DirectorySeparatorChar != '\\')
                        relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '\\');
                    _sendQueue.Enqueue((fullPath, relativePath));
                    try { totalBytes += new FileInfo(fullPath).Length; } catch { }
                    count++;
                }
                catch { /* skip inaccessible */ }
            }
        }
        if (count > 0)
        {
            _batchTotal = count;
            _batchTotalBytes = totalBytes;
            QueueChanged?.Invoke(count, totalBytes);
            _onLog?.Invoke("FileTransfer", "enqueued_folder", new object[] { Path.GetFileName(folderPath), count });
            _ = ProcessQueueAsync();
        }
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

    /// <summary>Приостановить всю очередь + текущий файл.</summary>
    public void PauseAll()
    {
        _queuePaused = true;
        foreach (var id in _outgoing.Keys.ToArray())
            Pause(id);
    }

    /// <summary>Возобновить очередь + все приостановленные файлы.</summary>
    public void ResumeAll()
    {
        _queuePaused = false;
        foreach (var id in _outgoing.Keys.ToArray())
            Resume(id);
        // Kick the queue processor in case it stopped due to pause.
        _ = ProcessQueueAsync();
    }

    /// <summary>Количество файлов в очереди + текущий.</summary>
    public int QueuedCount
    {
        get { lock (_queueLock) { return _sendQueue.Count + (_currentSendTransferId != null ? 1 : 0); } }
    }

    /// <summary>Отменить все передачи (при отключении сессии).</summary>
    public void CancelAll()
    {
        _queuePaused = false;
        lock (_queueLock) { _sendQueue.Clear(); }
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
            // Queue-level pause: wait until resumed before dequeuing next file.
            if (_queuePaused)
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
                if (_sendQueue.Count > 0 && !_queuePaused)
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
                Compressed = useCompression,
                BatchTotal = _batchTotal,
                BatchTotalBytes = _batchTotalBytes
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
                // UF-03 fix: ACK timeout — mark as Unconfirmed, not Completed.
                RaiseProgress(state, TransferStatus.Unconfirmed);
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
        catch (Exception ex) { _onLog?.Invoke("FileTransfer", "send_error_failed", new object[] { ex.Message }); }
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

            // F-01 fix: reject dangerous RelativePath to prevent path traversal.
            if (SafePath.IsDangerous(relativePath))
            {
                _onLog?.Invoke("FileTransfer", "incoming_rejected_dangerous_relative_path", new object[] { relativePath ?? "" });
                stream.Dispose();
                try { File.Delete(tempPath); } catch { }
                FireAndForget(_channel.SendFileErrorAsync(new FileErrorPayload { TransferId = payload.TransferId, Code = "path_rejected", Message = "Relative path rejected" }), "file_error_path_rejected");
                return;
            }
            // TargetDirectory is an absolute path (e.g. "D:\folder") — only block ".." traversal, not rooted paths.
            if (!string.IsNullOrEmpty(targetDir) && targetDir.Contains(".."))
            {
                _onLog?.Invoke("FileTransfer", "incoming_rejected_traversal_target_dir", new object[] { targetDir });
                targetDir = null; // fall back to default save dir
            }

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
                CurrentBytes = 0,
                BatchTotal = payload.BatchTotal,
                BatchTotalBytes = payload.BatchTotalBytes
            };
            RaiseProgress(item);
            _onLog?.Invoke("FileTransfer", "file_meta_received", new object[] { safeName, payload.FileSize });
        }
        catch (Exception ex)
        {
            _onLog?.Invoke("FileTransfer", "file_meta_receive_failed", new object[] { ex.Message });
            FireAndForget(_channel.SendFileErrorAsync(new FileErrorPayload
            {
                TransferId = payload.TransferId,
                Code = "receive_error",
                Message = ex.Message
            }), "file_error_receive");
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
            FireAndForget(_channel.SendFileErrorAsync(new FileErrorPayload { TransferId = payload.TransferId, Code = "chunk_error", Message = ex.Message }), "file_error_chunk_json");
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
            FireAndForget(_channel.SendFileErrorAsync(new FileErrorPayload { TransferId = transferId, Code = "chunk_error", Message = ex.Message }), "file_error_chunk_binary");
        }
    }

    private void OnFileEndReceived(FileEndPayload payload)
    {
        // Handler fires на DC thread (mrwebrtc SCTP dispatch). Conflict resolution
        // использует blocking .Wait(30s) для remote query response. Если выполнять
        // прямо здесь — DC thread блокируется, response от sender'а на тот же thread
        // НЕ обрабатывается → TCS никогда не set → 30s timeout каждый раз. Observed
        // live host logs 2026-04-19 21:37-21:38.
        //
        // Production: offload в ThreadPool через Task.Run → DC thread возвращается
        // немедленно и продолжает обрабатывать incoming messages.
        // Unit tests: _offloadFileEndToThreadPool=false → синхронно, SimulateFileEndReceived
        // не требует Task.Delay в assertion'ах.
        if (_offloadFileEndToThreadPool)
            _ = Task.Run(() => ProcessFileEndReceived(payload));
        else
            ProcessFileEndReceived(payload);
    }

    private void ProcessFileEndReceived(FileEndPayload payload)
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
                    // UF-04 fix: delete .part file on hash mismatch to prevent disk clutter.
                    try { if (File.Exists(state.TargetPath)) File.Delete(state.TargetPath); } catch { }
                    FireAndForget(_channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = false, Message = "Hash mismatch" }), "file_ack_hash_mismatch");
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
                // F-01 fix: verify final path stays inside saveDir after canonicalization.
                if (!SafePath.IsUnderRoot(saveDir, finalPath))
                {
                    _onLog?.Invoke("FileTransfer", "incoming_path_traversal_blocked", new object[] { finalPath, saveDir });
                    FireAndForget(_channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = false, Message = "Path traversal blocked" }), "file_ack_traversal");
                    try { File.Delete(state.TargetPath); } catch { }
                    return;
                }
                var dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            else
            {
                Directory.CreateDirectory(saveDir);
                finalPath = Path.Combine(saveDir, state.FileName);
            }

            // Conflict resolution priority (FT #3 refactor 2026-04-19):
            //   1. _conflictActionForAll — remembered OverwriteAll/SkipAll from previous file в этой же очереди.
            //   2. OnFileConflict local callback — legacy path, используется только тестами с mock channel.
            //   3. Remote query → await sender reply (production): отправить FileConflictQuery на sender,
            //      тот покажет dialog в FileTransferWindow, пришлёт FileConflictResponse. 30с timeout
            //      → fallback Rename. До исправления dialog всегда показывался на receiver в MainWindow —
            //      user'у sender'а приходилось угадывать что остановило transfer.
            if (File.Exists(finalPath))
            {
                ConflictAction action;
                // Lock serializes: one file at a time resolves conflict. Это гарантирует что
                // если первый файл получает OverwriteAll → устанавливает _conflictActionForAll,
                // следующие в batch'е увидят его и не отправят дубликат query. Без lock'а
                // параллельные OnFileEndReceived видят _conflictActionForAll=null одновременно.
                // ResolveConflictViaRemote блокирует до 30 сек (Wait на TCS) — OK, SCTP DC
                // уже доставил весь файл, никаких chunks не ждёт.
                lock (_conflictResolutionLock)
                {
                    if (_conflictActionForAll is not null)
                    {
                        action = _conflictActionForAll.Value;
                    }
                    else if (_canShowLocalDialogForIncoming && OnFileConflict is not null)
                    {
                        // Receiver = Viewer: user сам инициировал transfer и сидит за этим компьютером,
                        // dialog показывается локально через OnFileConflict callback.
                        action = OnFileConflict(finalPath);
                    }
                    else
                    {
                        // Receiver = Host (upload from viewer-sender): шлём query на viewer, тот покажет
                        // dialog у user'а, пришлёт response. Это исправление 2026-04-19: раньше dialog
                        // всегда на receiver — для upload PC→ноутбук он появлялся на ноутбуке (wrong).
                        action = ResolveConflictViaRemote(payload.TransferId, state.FileName);
                    }
                    if (action is ConflictAction.OverwriteAll) { _conflictActionForAll = ConflictAction.Overwrite; action = ConflictAction.Overwrite; }
                    if (action is ConflictAction.SkipAll) { _conflictActionForAll = ConflictAction.Skip; action = ConflictAction.Skip; }
                }

                switch (action)
                {
                    case ConflictAction.Overwrite:
                        // overwrite: File.Move with overwrite=true below
                        break;
                    case ConflictAction.Skip:
                        _onLog?.Invoke("FileTransfer", "file_skipped_conflict", new object[] { state.FileName });
                        try { File.Delete(state.TargetPath); } catch { }
                        FireAndForget(_channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = true, Message = "skipped" }), "file_ack_skipped");
                        RaiseCompleted(new TransferItem { TransferId = state.TransferId, FileName = state.FileName, Direction = TransferDirection.Incoming, Status = TransferStatus.Completed, TotalBytes = state.ExpectedSize, CurrentBytes = state.BytesWritten });
                        return;
                    case ConflictAction.Rename:
                    default:
                        finalPath = GetUniqueFilePath(Path.GetDirectoryName(finalPath) ?? saveDir, Path.GetFileName(finalPath));
                        break;
                }
            }
            File.Move(state.TargetPath, finalPath, overwrite: true);

            FireAndForget(_channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = true }), "file_ack_ok");
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
            FireAndForget(_channel.SendFileAckAsync(new FileAckPayload { TransferId = payload.TransferId, Success = false, Message = ex.Message }), "file_ack_error");
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
        {
            // Bug fix: signal the chunk-sending loop to stop (Bug 2).
            state.Cancelled = true;
            // Bug fix: complete the pending ack wait so the 30s timeout doesn't keep running (Bug 3).
            state.AckTcs.TrySetResult(false);
            RaiseFailed(state, payload.Message);
        }
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
        catch (Exception ex) { _onLog?.Invoke("FileTransfer", "abort_cleanup_failed", new object[] { ex.Message }); }
    }

    private static TransferItem ToTransferItem(OutgoingState state) =>
        new()
        {
            TransferId = state.TransferId,
            FileName = state.FileName,
            SourcePath = state.FilePath,
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
            SourcePath = state.FilePath,
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

    /// <summary>
    /// Максимальный размер декомпрессованного chunk'а. Типичный raw chunk = 64KB,
    /// даже при компрессии повторяющихся данных (наш LiveHost test видел ratio 1.3%)
    /// decompressed никогда не должен превышать ~1MB на единичный chunk.
    /// Лимит защищает от GZip-bomb: sender компрессит 1MB нулей в ~1KB и отправляет
    /// много таких chunks → получатель decompress'ит каждый в 1GB+ → OOM.
    /// </summary>
    internal const int MaxDecompressedChunkBytes = 1 * 1024 * 1024;

    internal static byte[] DecompressGZip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0;
        int read;
        while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxDecompressedChunkBytes)
                throw new InvalidDataException(
                    $"GZip decompressed chunk exceeded {MaxDecompressedChunkBytes} bytes (possible compression bomb). " +
                    $"Transfer aborted.");
            output.Write(buffer, 0, read);
        }
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

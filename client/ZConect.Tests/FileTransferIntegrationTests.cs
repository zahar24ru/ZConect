using System.Collections.Concurrent;
using System.Security.Cryptography;
using FileTransfer;
using WebRtcTransport;
using Xunit;

namespace ZConect.Tests;

#region MockFileTransferChannel

/// <summary>
/// Mock channel that records all sent payloads and allows firing received events.
/// </summary>
public sealed class MockFileTransferChannel : IFileTransferChannel
{
    // --- Sent payloads ---
    public ConcurrentBag<FileMetaPayload> SentMetas { get; } = new();
    public ConcurrentBag<(string TransferId, int Sequence, byte[] Data, bool Compressed)> SentBinaryChunks { get; } = new();
    public ConcurrentBag<FileChunkPayload> SentChunks { get; } = new();
    public ConcurrentBag<FileEndPayload> SentEnds { get; } = new();
    public ConcurrentBag<FileAckPayload> SentAcks { get; } = new();
    public ConcurrentBag<FileErrorPayload> SentErrors { get; } = new();

    // --- IFileTransferChannel Send methods ---
    public Task SendFileMetaAsync(FileMetaPayload payload, CancellationToken ct = default)
    {
        SentMetas.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendFileChunkAsync(FileChunkPayload payload, CancellationToken ct = default)
    {
        SentChunks.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendBinaryFileChunkAsync(string transferId, int sequence, byte[] data, bool compressed, CancellationToken ct = default)
    {
        SentBinaryChunks.Add((transferId, sequence, data, compressed));
        return Task.CompletedTask;
    }

    public Task SendFileEndAsync(FileEndPayload payload, CancellationToken ct = default)
    {
        SentEnds.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendFileAckAsync(FileAckPayload payload, CancellationToken ct = default)
    {
        SentAcks.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendFileErrorAsync(FileErrorPayload payload, CancellationToken ct = default)
    {
        SentErrors.Add(payload);
        return Task.CompletedTask;
    }

    public ConcurrentBag<FileConflictQueryPayload> SentConflictQueries { get; } = new();
    public ConcurrentBag<FileConflictResponsePayload> SentConflictResponses { get; } = new();

    public Task SendFileConflictQueryAsync(FileConflictQueryPayload payload, CancellationToken ct = default)
    {
        SentConflictQueries.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendFileConflictResponseAsync(FileConflictResponsePayload payload, CancellationToken ct = default)
    {
        SentConflictResponses.Add(payload);
        return Task.CompletedTask;
    }

    // --- Events ---
    public event Action<FileMetaPayload>? FileMetaReceived;
    public event Action<FileChunkPayload>? FileChunkReceived;
    public event Action<string, int, bool, byte[]>? BinaryFileChunkReceived;
    public event Action<FileEndPayload>? FileEndReceived;
    public event Action<FileAckPayload>? FileAckReceived;
    public event Action<FileErrorPayload>? FileErrorReceived;
    public event Action<FileConflictQueryPayload>? FileConflictQueryReceived;
    public event Action<FileConflictResponsePayload>? FileConflictResponseReceived;

    // --- Simulate methods (fire events) ---
    public void SimulateFileMetaReceived(FileMetaPayload payload) => FileMetaReceived?.Invoke(payload);
    public void SimulateFileChunkReceived(FileChunkPayload payload) => FileChunkReceived?.Invoke(payload);
    public void SimulateBinaryFileChunkReceived(string transferId, int sequence, bool compressed, byte[] data)
        => BinaryFileChunkReceived?.Invoke(transferId, sequence, compressed, data);
    public void SimulateFileEndReceived(FileEndPayload payload) => FileEndReceived?.Invoke(payload);
    public void SimulateFileAckReceived(FileAckPayload payload) => FileAckReceived?.Invoke(payload);
    public void SimulateFileErrorReceived(FileErrorPayload payload) => FileErrorReceived?.Invoke(payload);
    public void SimulateFileConflictQueryReceived(FileConflictQueryPayload payload) => FileConflictQueryReceived?.Invoke(payload);
    public void SimulateFileConflictResponseReceived(FileConflictResponsePayload payload) => FileConflictResponseReceived?.Invoke(payload);
}

#endregion

/// <summary>
/// Integration tests for FileTransferService upload (send) and download (receive) flows.
/// Uses MockFileTransferChannel and real temp directories.
/// </summary>
public sealed class FileTransferIntegrationTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _tempDir;
    private readonly string _saveDir;
    private readonly MockFileTransferChannel _channel;
    private readonly List<string> _logs = new();

    public FileTransferIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "FTTests_" + Guid.NewGuid().ToString("N")[..8]);
        _tempDir = Path.Combine(_testDir, "IncomingTemp");
        _saveDir = Path.Combine(_testDir, "Saved");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_saveDir);
        _channel = new MockFileTransferChannel();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { /* best effort */ }
    }

    private FileTransferService CreateService(Func<string>? getOutgoingTargetDir = null)
    {
        return new FileTransferService(
            _channel,
            chunkSizeBytes: 1024,
            getIncomingTempDir: () => _tempDir,
            getIncomingSaveDir: () => _saveDir,
            getOutgoingTargetDir: getOutgoingTargetDir,
            onLog: (category, key, args) =>
            {
                lock (_logs) { _logs.Add($"{category}:{key}:{string.Join(",", args)}"); }
            },
            offloadFileEndToThreadPool: false);
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    /// <summary>Create a temp file with random content and return (path, content, hash).</summary>
    private (string Path, byte[] Content, string Hash) CreateTempFile(string fileName, int size = 256)
    {
        var outDir = Path.Combine(_testDir, "OutFiles");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, fileName);
        var content = new byte[size];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);
        return (path, content, ComputeSha256(content));
    }

    /// <summary>
    /// Simulate a complete file receive: file_meta + binary chunks + file_end.
    /// Returns the transferId used.
    /// </summary>
    private string SimulateIncomingFile(
        FileTransferService service,
        string fileName,
        byte[] content,
        string? hash = null,
        string? relativePath = null,
        string? targetDirectory = null)
    {
        var transferId = Guid.NewGuid().ToString("N");
        var actualHash = hash ?? ComputeSha256(content);

        _channel.SimulateFileMetaReceived(new FileMetaPayload
        {
            TransferId = transferId,
            FileName = fileName,
            FileSize = content.Length,
            Hash = actualHash,
            RelativePath = relativePath,
            TargetDirectory = targetDirectory,
            Compressed = false
        });

        // Send binary chunks
        var chunkSize = 1024;
        var seq = 0;
        for (var offset = 0; offset < content.Length; offset += chunkSize)
        {
            var len = Math.Min(chunkSize, content.Length - offset);
            var chunk = new byte[len];
            Array.Copy(content, offset, chunk, 0, len);
            _channel.SimulateBinaryFileChunkReceived(transferId, seq++, false, chunk);
        }

        _channel.SimulateFileEndReceived(new FileEndPayload { TransferId = transferId });
        return transferId;
    }

    // ================================================================
    // A. Download (Receive) tests
    // ================================================================

    [Fact]
    public void A01_Receive_normal_file_saved_with_correct_hash_and_ack_sent()
    {
        var service = CreateService();
        var content = new byte[500];
        Random.Shared.NextBytes(content);
        var hash = ComputeSha256(content);
        var completedItems = new List<TransferItem>();
        service.TransferCompleted += item => completedItems.Add(item);

        SimulateIncomingFile(service, "testfile.bin", content, hash);

        // Verify file saved
        var savedPath = Path.Combine(_saveDir, "testfile.bin");
        Assert.True(File.Exists(savedPath), $"File should exist at {savedPath}");
        var savedContent = File.ReadAllBytes(savedPath);
        Assert.Equal(content, savedContent);

        // Verify ack sent with Success=true
        Assert.Contains(_channel.SentAcks, a => a.Success);

        // Verify completed event fired
        Assert.Single(completedItems);
        Assert.Equal(TransferStatus.Completed, completedItems[0].Status);
        Assert.Equal(TransferDirection.Incoming, completedItems[0].Direction);
    }

    [Fact]
    public void A02_Receive_file_with_spaces_in_name()
    {
        var service = CreateService();
        var content = new byte[100];
        Random.Shared.NextBytes(content);

        SimulateIncomingFile(service, "My Document.pdf", content);

        var savedPath = Path.Combine(_saveDir, "My Document.pdf");
        Assert.True(File.Exists(savedPath), "File with spaces in name should be saved");
        Assert.Equal(content, File.ReadAllBytes(savedPath));
    }

    [Fact]
    public void A03_Receive_file_with_dots_in_name()
    {
        var service = CreateService();
        var content = new byte[100];
        Random.Shared.NextBytes(content);

        SimulateIncomingFile(service, "report.final.v2..pdf", content);

        var savedPath = Path.Combine(_saveDir, "report.final.v2..pdf");
        Assert.True(File.Exists(savedPath), "File with dots in name should be saved");
        Assert.Equal(content, File.ReadAllBytes(savedPath));
    }

    [Fact]
    public void A04_Receive_file_in_subdirectory()
    {
        var service = CreateService();
        var content = new byte[100];
        Random.Shared.NextBytes(content);

        SimulateIncomingFile(service, "file.txt", content, relativePath: @"subfolder\file.txt");

        var savedPath = Path.Combine(_saveDir, "subfolder", "file.txt");
        Assert.True(File.Exists(savedPath), "File in subdirectory should be saved");
        Assert.Equal(content, File.ReadAllBytes(savedPath));
    }

    [Fact]
    public void A05_Receive_file_with_cyrillic_name()
    {
        var service = CreateService();
        var content = new byte[100];
        Random.Shared.NextBytes(content);
        var fileName = "\u0417\u0430\u044F\u0432\u043A\u0430 \u043D\u0430 \u043F\u043E\u043B\u0443\u0447\u0435\u043D\u0438\u0435.pdf";

        SimulateIncomingFile(service, fileName, content);

        var savedPath = Path.Combine(_saveDir, fileName);
        Assert.True(File.Exists(savedPath), "File with Cyrillic name should be saved");
        Assert.Equal(content, File.ReadAllBytes(savedPath));
    }

    [Fact]
    public void A06_Reject_path_traversal()
    {
        var service = CreateService();
        var content = new byte[50];
        Random.Shared.NextBytes(content);
        var transferId = Guid.NewGuid().ToString("N");

        _channel.SimulateFileMetaReceived(new FileMetaPayload
        {
            TransferId = transferId,
            FileName = "passwd",
            FileSize = content.Length,
            Hash = ComputeSha256(content),
            RelativePath = @"..\..\..\etc\passwd"
        });

        // Should send file_error
        Assert.Contains(_channel.SentErrors, e => e.TransferId == transferId && e.Code == "path_rejected");
        // File should not be saved
        Assert.False(File.Exists(Path.Combine(_saveDir, "passwd")));
    }

    [Fact]
    public void A07_Reject_reserved_name()
    {
        var service = CreateService();
        var content = new byte[50];
        Random.Shared.NextBytes(content);
        var transferId = Guid.NewGuid().ToString("N");

        // SafePath.IsDangerous checks reserved names in relativePath components
        _channel.SimulateFileMetaReceived(new FileMetaPayload
        {
            TransferId = transferId,
            FileName = "COM1.txt",
            FileSize = content.Length,
            Hash = ComputeSha256(content),
            RelativePath = "COM1.txt"
        });

        // Should be rejected via SafePath.IsDangerous
        Assert.Contains(_channel.SentErrors, e => e.TransferId == transferId);
    }

    [Fact]
    public void A08_Reject_very_long_path()
    {
        var service = CreateService();
        var content = new byte[50];
        Random.Shared.NextBytes(content);
        var transferId = Guid.NewGuid().ToString("N");
        var longPath = new string('a', 501) + ".txt";

        _channel.SimulateFileMetaReceived(new FileMetaPayload
        {
            TransferId = transferId,
            FileName = "file.txt",
            FileSize = content.Length,
            Hash = ComputeSha256(content),
            RelativePath = longPath
        });

        // Should be rejected (IsDangerous returns true for path > 500 chars)
        Assert.Contains(_channel.SentErrors, e => e.TransferId == transferId);
    }

    [Fact]
    public void A09_Hash_mismatch_deletes_file_and_sends_nack()
    {
        var service = CreateService();
        var content = new byte[100];
        Random.Shared.NextBytes(content);
        var wrongHash = "0000000000000000000000000000000000000000000000000000000000000000";
        var failedItems = new List<TransferItem>();
        service.TransferFailed += item => failedItems.Add(item);

        SimulateIncomingFile(service, "hashtest.bin", content, hash: wrongHash);

        // File should NOT exist (deleted on mismatch)
        var savedPath = Path.Combine(_saveDir, "hashtest.bin");
        Assert.False(File.Exists(savedPath), "File should be deleted on hash mismatch");

        // .part temp file should also be deleted
        var partFiles = Directory.GetFiles(_tempDir, "*.part");
        Assert.Empty(partFiles);

        // Nack sent (Success = false)
        Assert.Contains(_channel.SentAcks, a => !a.Success && a.Message != null && a.Message.Contains("Hash"));

        // Failed event fired
        Assert.Single(failedItems);
    }

    [Fact]
    public void A10_File_already_exists_conflict_resolution_rename()
    {
        var service = CreateService();
        var content1 = new byte[100];
        Random.Shared.NextBytes(content1);
        var content2 = new byte[100];
        Random.Shared.NextBytes(content2);

        // First file
        SimulateIncomingFile(service, "duplicate.bin", content1);
        Assert.True(File.Exists(Path.Combine(_saveDir, "duplicate.bin")));

        // Default conflict = Rename. Send same filename again.
        SimulateIncomingFile(service, "duplicate.bin", content2);

        // Original file still has original content
        Assert.Equal(content1, File.ReadAllBytes(Path.Combine(_saveDir, "duplicate.bin")));
        // Renamed file should exist
        var renamedPath = Path.Combine(_saveDir, "duplicate (1).bin");
        Assert.True(File.Exists(renamedPath), "Renamed file should exist");
        Assert.Equal(content2, File.ReadAllBytes(renamedPath));
    }

    [Fact]
    public void A10b_File_already_exists_conflict_overwrite()
    {
        var service = CreateService();
        service.OnFileConflict = _ => ConflictAction.Overwrite;

        var content1 = new byte[100];
        Random.Shared.NextBytes(content1);
        var content2 = new byte[100];
        Random.Shared.NextBytes(content2);

        SimulateIncomingFile(service, "overwrite.bin", content1);
        SimulateIncomingFile(service, "overwrite.bin", content2);

        // File should have second content (overwritten)
        Assert.Equal(content2, File.ReadAllBytes(Path.Combine(_saveDir, "overwrite.bin")));
    }

    [Fact]
    public void A10c_File_already_exists_conflict_skip()
    {
        var service = CreateService();
        service.OnFileConflict = _ => ConflictAction.Skip;

        var content1 = new byte[100];
        Random.Shared.NextBytes(content1);
        var content2 = new byte[100];
        Random.Shared.NextBytes(content2);

        SimulateIncomingFile(service, "skipme.bin", content1);
        SimulateIncomingFile(service, "skipme.bin", content2);

        // File should still have first content (skipped)
        Assert.Equal(content1, File.ReadAllBytes(Path.Combine(_saveDir, "skipme.bin")));
        // No renamed file
        Assert.False(File.Exists(Path.Combine(_saveDir, "skipme (1).bin")));
    }

    // ================================================================
    // B. Upload (Send) tests
    // ================================================================

    [Fact]
    public async Task B11_Send_normal_file()
    {
        var service = CreateService();
        var (path, content, hash) = CreateTempFile("sendme.bin", 2048);
        var completedItems = new List<TransferItem>();
        service.TransferCompleted += item => completedItems.Add(item);

        service.EnqueueSend(path);

        // Wait for meta to be sent
        await WaitUntil(() => _channel.SentMetas.Count > 0, timeout: 5000);

        var meta = _channel.SentMetas.First();
        Assert.Equal("sendme.bin", meta.FileName);
        Assert.Equal(content.Length, meta.FileSize);
        Assert.Equal(hash, meta.Hash);

        // Wait for file_end
        await WaitUntil(() => _channel.SentEnds.Count > 0, timeout: 5000);

        // Chunks should have been sent (2048 bytes / 1024 chunk = 2 chunks)
        Assert.True(_channel.SentBinaryChunks.Count >= 2);

        // Simulate ACK from receiver
        _channel.SimulateFileAckReceived(new FileAckPayload
        {
            TransferId = meta.TransferId,
            Success = true
        });

        await WaitUntil(() => completedItems.Count > 0, timeout: 5000);
        Assert.Equal(TransferStatus.Completed, completedItems[0].Status);
    }

    [Fact]
    public async Task B12_Send_file_with_spaces()
    {
        var service = CreateService();
        var (path, _, _) = CreateTempFile("My File.txt", 100);

        service.EnqueueSend(path);
        await WaitUntil(() => _channel.SentMetas.Count > 0, timeout: 5000);

        Assert.Equal("My File.txt", _channel.SentMetas.First().FileName);
    }

    [Fact]
    public void B13_Send_nonexistent_file_no_crash()
    {
        var service = CreateService();
        var fakePath = Path.Combine(_testDir, "does_not_exist.txt");

        // Should not throw
        service.EnqueueSend(fakePath);

        // Nothing sent
        Assert.Empty(_channel.SentMetas);

        // Log recorded
        lock (_logs)
        {
            Assert.Contains(_logs, l => l.Contains("enqueue_file_not_found"));
        }
    }

    [Fact]
    public async Task B14_Send_empty_file()
    {
        var service = CreateService();
        var (path, _, hash) = CreateTempFile("empty.bin", 0);

        service.EnqueueSend(path);
        await WaitUntil(() => _channel.SentMetas.Count > 0, timeout: 5000);

        var meta = _channel.SentMetas.First();
        Assert.Equal("empty.bin", meta.FileName);
        Assert.Equal(0, meta.FileSize);

        // file_end should be sent (no chunks for empty file)
        await WaitUntil(() => _channel.SentEnds.Count > 0, timeout: 5000);

        // No binary chunks for empty file
        var chunksForTransfer = _channel.SentBinaryChunks
            .Where(c => c.TransferId == meta.TransferId).ToList();
        Assert.Empty(chunksForTransfer);

        // Simulate ACK
        _channel.SimulateFileAckReceived(new FileAckPayload
        {
            TransferId = meta.TransferId,
            Success = true
        });
    }

    [Fact]
    public async Task B15_Send_and_receive_ack_marks_complete()
    {
        var service = CreateService();
        var (path, _, _) = CreateTempFile("acktest.bin", 128);
        var completedItems = new List<TransferItem>();
        service.TransferCompleted += item => completedItems.Add(item);

        service.EnqueueSend(path);
        await WaitUntil(() => _channel.SentEnds.Count > 0, timeout: 5000);

        var meta = _channel.SentMetas.First();

        // Simulate successful ACK
        _channel.SimulateFileAckReceived(new FileAckPayload
        {
            TransferId = meta.TransferId,
            Success = true
        });

        await WaitUntil(() => completedItems.Count > 0, timeout: 5000);
        Assert.Equal(TransferStatus.Completed, completedItems[0].Status);
        Assert.Equal(TransferDirection.Outgoing, completedItems[0].Direction);
    }

    [Fact]
    public async Task B15b_Send_and_receive_nack_marks_failed()
    {
        var service = CreateService();
        var (path, _, _) = CreateTempFile("nacktest.bin", 128);
        var failedItems = new List<TransferItem>();
        service.TransferFailed += item => failedItems.Add(item);

        service.EnqueueSend(path);
        await WaitUntil(() => _channel.SentEnds.Count > 0, timeout: 5000);

        var meta = _channel.SentMetas.First();

        // Simulate NACK
        _channel.SimulateFileAckReceived(new FileAckPayload
        {
            TransferId = meta.TransferId,
            Success = false,
            Message = "Hash mismatch on receiver"
        });

        await WaitUntil(() => failedItems.Count > 0, timeout: 5000);
        Assert.Equal(TransferStatus.Failed, failedItems[0].Status);
    }

    // ================================================================
    // C. Edge cases
    // ================================================================

    [Fact]
    public async Task C16_File_deleted_during_send_graceful_error()
    {
        var service = CreateService();
        var (path, _, _) = CreateTempFile("deleteme.bin", 4096);
        var failedItems = new List<TransferItem>();
        service.TransferFailed += item => failedItems.Add(item);

        // Delete the file before enqueueing so hashing fails
        File.Delete(path);

        // EnqueueSend checks File.Exists first, so nothing is enqueued
        service.EnqueueSend(path);
        Assert.Empty(_channel.SentMetas);

        // Now test deletion after enqueue but before chunks finish:
        // Create a large file so sending takes time
        var largePath = Path.Combine(_testDir, "OutFiles", "largefile.bin");
        var largeContent = new byte[64 * 1024]; // big enough to take >1 chunk
        Random.Shared.NextBytes(largeContent);
        File.WriteAllBytes(largePath, largeContent);

        service.EnqueueSend(largePath);

        // Immediately delete the file — hashing may fail or chunk read may fail
        await Task.Delay(10);
        try { File.Delete(largePath); } catch { /* may be locked */ }

        // Wait for either completion or failure
        await WaitUntil(() => failedItems.Count > 0 || _channel.SentEnds.Count > 0, timeout: 10000);

        // If it failed, that's the expected graceful handling
        // If it completed (file was read before delete), that's also fine
        // The key test is: no unhandled exception
    }

    [Fact]
    public void C17_Receive_chunks_without_meta_ignored()
    {
        var service = CreateService();
        var unknownTransferId = Guid.NewGuid().ToString("N");
        var chunk = new byte[100];
        Random.Shared.NextBytes(chunk);

        // Should not throw
        _channel.SimulateBinaryFileChunkReceived(unknownTransferId, 0, false, chunk);

        // Should be logged
        lock (_logs)
        {
            Assert.Contains(_logs, l => l.Contains("binary_chunk_without_meta"));
        }

        // No acks or errors sent for unknown chunks
        Assert.Empty(_channel.SentAcks);
    }

    [Fact]
    public void C18_Double_file_end_no_crash()
    {
        var service = CreateService();
        var content = new byte[100];
        Random.Shared.NextBytes(content);
        var transferId = Guid.NewGuid().ToString("N");
        var hash = ComputeSha256(content);

        // Send meta
        _channel.SimulateFileMetaReceived(new FileMetaPayload
        {
            TransferId = transferId,
            FileName = "double_end.bin",
            FileSize = content.Length,
            Hash = hash,
            Compressed = false
        });

        // Send chunk
        _channel.SimulateBinaryFileChunkReceived(transferId, 0, false, content);

        // First file_end -- should succeed
        _channel.SimulateFileEndReceived(new FileEndPayload { TransferId = transferId });

        // Second file_end -- should not crash (transfer already removed)
        _channel.SimulateFileEndReceived(new FileEndPayload { TransferId = transferId });

        // File should still be saved from first end
        Assert.True(File.Exists(Path.Combine(_saveDir, "double_end.bin")));

        // Log the second one as "file_end_without_transfer"
        lock (_logs)
        {
            Assert.Contains(_logs, l => l.Contains("file_end_without_transfer"));
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static async Task WaitUntil(Func<bool> condition, int timeout = 5000)
    {
        var deadline = Environment.TickCount64 + timeout;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(25);
        // One final check — if still false, let calling Assert handle it
    }
}

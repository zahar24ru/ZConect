using System.Diagnostics;
using System.Security.Cryptography;
using FileTransfer;
using SessionClient;
using WebRtcTransport;
using Xunit;
using Xunit.Abstractions;

namespace ZConect.Tests;

/// <summary>
/// End-to-end FT test: viewer connects to live host, creates remote folder, uploads 3 files
/// of different sizes, downloads them back, re-downloads to trigger conflict dialog
/// (OnFileConflict callback on viewer-receiver since viewer=receiver in download), then cleans up.
///
/// How to run:
///   1. On host (ноутбук): start ZConnect → Create Session → write login + pass codes.
///   2. Locally: set env vars:
///        set ZCONECT_HOST_LOGIN=12345678
///        set ZCONECT_HOST_PASS=87654321
///        set ZCONECT_HOST_SAVE_DIR=C:\Users\zahar\Downloads\ZConectReceived   (host's incoming save dir)
///        set ZCONECT_SERVER_URL=http://92.63.102.244:8080                      (optional; default)
///   3. dotnet test --filter "FullyQualifiedName~LiveFileTransferConflictTests"
///
/// Covers:
///   - CreateFolderRequest on host (absolute path).
///   - Upload 3 files via FileTransferService.EnqueueSend (1KB, 64KB, 1MB random).
///   - DirList verification (3 files present on host with correct sizes).
///   - FileRequest download — round-trip integrity via SHA256.
///   - Conflict flow: re-download → OnFileConflict called 3 times → Overwrite → files replaced.
///   - Cleanup: DeleteRequest on host folder + local test dirs removed.
/// </summary>
[Trait("Category", "LiveHost")]
[Collection("LiveHost")]
public sealed class LiveFileTransferConflictTests : IDisposable
{
    private const string DefaultServerUrl = "http://92.63.102.244:8080";
    private const string LocalTestRoot = @"C:\Test_downloads";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SessionApiClient _api;
    private readonly ITestOutputHelper _out;
    private readonly Stopwatch _sw = new();

    public LiveFileTransferConflictTests(ITestOutputHelper output)
    {
        _out = output;
        _api = new SessionApiClient(_http, msg => Log($"[API] {msg}"));
    }

    public void Dispose() => _http.Dispose();

    private void Log(string msg)
    {
        try { _out.WriteLine(msg); } catch { }
        Console.WriteLine(msg);
    }

    private void Mark(string name) => Log($"[+{_sw.ElapsedMilliseconds,7}ms] {name}");

    private (string login, string pass, string serverUrl, string hostSaveDir)? TryGetConfig()
    {
        var login = Environment.GetEnvironmentVariable("ZCONECT_HOST_LOGIN");
        var pass = Environment.GetEnvironmentVariable("ZCONECT_HOST_PASS");
        var server = Environment.GetEnvironmentVariable("ZCONECT_SERVER_URL") ?? DefaultServerUrl;
        var hostSaveDir = Environment.GetEnvironmentVariable("ZCONECT_HOST_SAVE_DIR");
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(pass) || string.IsNullOrWhiteSpace(hostSaveDir))
        {
            Log("SKIPPED: set ZCONECT_HOST_LOGIN, ZCONECT_HOST_PASS, ZCONECT_HOST_SAVE_DIR env vars.");
            Log("  Example: ZCONECT_HOST_SAVE_DIR=C:\\Users\\<user>\\Downloads\\ZConectReceived");
            return null;
        }
        return (login!, pass!, server, hostSaveDir!);
    }

    private static string ResolveWsUrl(string serverApiBaseUrl, string wsUrlFromResponse)
    {
        if (string.IsNullOrWhiteSpace(wsUrlFromResponse)) wsUrlFromResponse = "/ws";
        if (wsUrlFromResponse.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || wsUrlFromResponse.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return wsUrlFromResponse;
        var baseUri = new Uri(serverApiBaseUrl);
        var scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{scheme}://{baseUri.Host}:{baseUri.Port}{wsUrlFromResponse}";
    }

    [Fact]
    public async Task FT_full_cycle_upload_download_conflict_overwrite()
    {
        var cfg = TryGetConfig();
        if (cfg is null) return;
        var (loginCode, passCode, serverUrl, hostSaveDir) = cfg.Value;

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = lifetime.Token;
        _sw.Start();

        // ─── Local test directories ────────────────────────────────────
        var viewerSourceDir = Path.Combine(LocalTestRoot, "source");
        var viewerDownloadsDir = Path.Combine(LocalTestRoot, "downloads");
        Directory.CreateDirectory(viewerSourceDir);
        Directory.CreateDirectory(viewerDownloadsDir);
        // Clean stale artifacts from previous failed runs.
        foreach (var f in Directory.GetFiles(viewerDownloadsDir)) try { File.Delete(f); } catch { }
        foreach (var f in Directory.GetFiles(viewerSourceDir)) try { File.Delete(f); } catch { }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var remoteFolderName = $"Test_downloads_{timestamp}";
        var remoteFolderFullPath = Path.Combine(hostSaveDir, remoteFolderName);
        Log($"remote test folder: {remoteFolderFullPath}");
        Log($"local source:     {viewerSourceDir}");
        Log($"local downloads:  {viewerDownloadsDir}");

        // ─── WebRTC join (minimal — no video in this FT-focused test) ──
        var join = await _api.JoinSessionAsync(serverUrl, loginCode, passCode, ct);
        Assert.NotNull(join);
        Mark("join_ok");

        var settings = new TransportSettings
        {
            StunUrl = "stun:stun.l.google.com:19302",
            TurnUrl = Environment.GetEnvironmentVariable("ZCONECT_TURN_URL") ?? "",
            TurnUsername = Environment.GetEnvironmentVariable("ZCONECT_TURN_USER") ?? "",
            TurnPassword = Environment.GetEnvironmentVariable("ZCONECT_TURN_PASS") ?? "",
        };
        var peer = new MixedRealityPeerConnectionAgent(settings, msg => Log($"[peer] {msg}"));
        var dataAgent = new MixedRealityDataChannelAgent();
        peer.DataChannelAdded += dataAgent.AttachChannel;

        try
        {
            await peer.InitializeAsync(ct, includeVideoTransceiver: true);
            Mark("peer_initialized");

            var dc = new DataChannelCoordinator(dataAgent, msg => Log($"[dc] {msg}"));
            var fileOpenedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.SubscribeChannelOpened(kind =>
            {
                Mark($"dc_opened_{kind}");
                if (kind == DataChannelKind.File) fileOpenedTcs.TrySetResult(true);
            });

            await using var ws = new WebSocketSignalingClient();
            var hostPeerStateTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ws.MessageReceived += msg => { if (msg.Type == "peer_state") hostPeerStateTcs.TrySetResult(true); };

            var wsUrl = ResolveWsUrl(serverUrl, join!.WsUrl);
            await ws.ConnectAsync(wsUrl, join.SessionId, join.WsToken, ct);
            var coord = new SignalingCoordinator(ws, peer, msg => Log($"[sig] {msg}"));
            coord.SetSession(join.SessionId);
            await ws.SendAsync("peer_state", join.SessionId, new { state = "joined" }, ct);
            try { using var pw = CancellationTokenSource.CreateLinkedTokenSource(ct); pw.CancelAfter(TimeSpan.FromSeconds(5)); await hostPeerStateTcs.Task.WaitAsync(pw.Token); } catch { }
            await coord.StartAsCallerAsync(join.SessionId, ct);
            await fileOpenedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Mark("dc_file_open");

            // mrwebrtc race: ChannelOpened может прийти до реального state=Open. Retry небольшой.
            async Task SendWithRetryAsync(Func<Task> send, string name)
            {
                for (var retry = 0; retry < 10; retry++)
                {
                    try { await send(); return; }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("is not open"))
                    {
                        await Task.Delay(100 * (retry + 1), ct);
                    }
                }
                await send();
            }

            // ─── Viewer-side FileTransferService ────────────────────────
            var channel = new DataChannelFileTransferChannel(dc);
            var conflictsSeen = new System.Collections.Concurrent.ConcurrentBag<string>();
            var completedOutgoing = new System.Collections.Concurrent.ConcurrentBag<TransferItem>();
            var completedIncoming = new System.Collections.Concurrent.ConcurrentBag<TransferItem>();
            var failedTransfers = new System.Collections.Concurrent.ConcurrentBag<string>();

            var fts = new FileTransferService(
                channel,
                getIncomingSaveDir: () => viewerDownloadsDir,
                canShowLocalDialogForIncoming: true,
                onLog: (cat, ev, args) => Log($"[fts] {cat}: {ev} {string.Join(" ", args)}"));
            fts.OnFileConflict = path =>
            {
                conflictsSeen.Add(Path.GetFileName(path));
                Log($"[CONFLICT] {Path.GetFileName(path)} → Overwrite");
                return ConflictAction.Overwrite;
            };
            fts.TransferCompleted += item =>
            {
                if (item.Direction == TransferDirection.Outgoing) completedOutgoing.Add(item);
                else completedIncoming.Add(item);
            };
            fts.TransferFailed += item =>
            {
                failedTransfers.Add($"{item.FileName}: {item.ErrorMessage}");
            };

            // ─── 1. Create remote folder on host ───────────────────────
            var createFolderRid = Guid.NewGuid().ToString("N");
            var createFolderTcs = new TaskCompletionSource<CreateFolderResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.CreateFolderResponseReceived += resp =>
            {
                if (resp.RequestId == createFolderRid) createFolderTcs.TrySetResult(resp);
            };
            await SendWithRetryAsync(() => dc.SendCreateFolderRequestAsync(new CreateFolderRequestPayload
            {
                ParentPath = hostSaveDir,
                FolderName = remoteFolderName,
                RequestId = createFolderRid,
            }, ct), "create_folder");
            var createResp = await createFolderTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(createResp.Success, $"CreateFolder failed: {createResp.Message}");
            Mark("remote_folder_created");

            // ─── 2. Generate 3 test files, different sizes ─────────────
            var files = new (string Name, int Size)[]
            {
                ("small.txt",   1024),         // 1 KB
                ("medium.bin",  64 * 1024),    // 64 KB
                ("large.bin",   1024 * 1024),  // 1 MB
            };
            var sourceHashes = new Dictionary<string, string>();
            foreach (var (name, size) in files)
            {
                var bytes = new byte[size];
                RandomNumberGenerator.Fill(bytes);
                var src = Path.Combine(viewerSourceDir, name);
                await File.WriteAllBytesAsync(src, bytes, ct);
                sourceHashes[name] = Convert.ToHexString(SHA256.HashData(bytes));
                Log($"  generated {name} = {size} bytes, sha256={sourceHashes[name][..16]}...");
            }
            Mark("source_files_generated");

            // ─── 3. Upload (viewer → host, relativePath = "<folder>/<name>") ──
            fts.SetBatchInfo(totalFiles: 3, totalBytes: files.Sum(f => (long)f.Size));
            foreach (var (name, _) in files)
            {
                var src = Path.Combine(viewerSourceDir, name);
                fts.EnqueueSend(src, relativePath: $"{remoteFolderName}/{name}");
            }

            // Wait for 3 outgoing completions or failure.
            var uploadDeadline = DateTime.UtcNow.AddMinutes(2);
            while (completedOutgoing.Count < 3 && failedTransfers.IsEmpty && DateTime.UtcNow < uploadDeadline)
                await Task.Delay(200, ct);
            Assert.True(failedTransfers.IsEmpty, $"Upload failures: {string.Join("; ", failedTransfers)}");
            Assert.Equal(3, completedOutgoing.Count);
            Mark("upload_completed_3of3");

            // ─── 4. Verify via DirList ─────────────────────────────────
            var dirListRid = Guid.NewGuid().ToString("N");
            var dirListTcs = new TaskCompletionSource<DirListResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.DirListResponseReceived += resp =>
            {
                if (resp.RequestId == dirListRid) dirListTcs.TrySetResult(resp);
            };
            await SendWithRetryAsync(() => dc.SendDirListRequestAsync(new DirListRequestPayload
            {
                Path = remoteFolderFullPath,
                RequestId = dirListRid,
            }, ct), "dir_list");
            var dirResp = await dirListTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(3, dirResp.Items.Count);
            foreach (var (name, size) in files)
            {
                var item = dirResp.Items.FirstOrDefault(i => i.Name == name);
                Assert.NotNull(item);
                Assert.Equal((long)size, item!.Size);
            }
            Mark("dir_list_verified_3of3");

            // ─── 5. Download files (first time, no conflict) ───────────
            foreach (var (name, _) in files)
            {
                await SendWithRetryAsync(() => dc.SendFileRequestAsync(new FileRequestPayload
                {
                    Path = Path.Combine(remoteFolderFullPath, name),
                    RequestId = Guid.NewGuid().ToString("N"),
                }, ct), $"file_request_{name}");
            }

            var downloadDeadline = DateTime.UtcNow.AddMinutes(2);
            while (completedIncoming.Count < 3 && failedTransfers.IsEmpty && DateTime.UtcNow < downloadDeadline)
                await Task.Delay(200, ct);
            Assert.True(failedTransfers.IsEmpty, $"Download failures: {string.Join("; ", failedTransfers)}");
            Assert.Equal(3, completedIncoming.Count);
            Mark("download_completed_3of3");

            // ─── 6. Verify SHA256 round-trip ───────────────────────────
            foreach (var (name, _) in files)
            {
                var downloaded = Path.Combine(viewerDownloadsDir, name);
                Assert.True(File.Exists(downloaded), $"Download missing: {downloaded}");
                var bytes = await File.ReadAllBytesAsync(downloaded, ct);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                Assert.Equal(sourceHashes[name], hash);
            }
            Mark("sha256_integrity_ok");

            // ─── 7. Re-download → conflict → OnFileConflict → Overwrite ──
            completedIncoming.Clear();
            conflictsSeen.Clear();
            foreach (var (name, _) in files)
            {
                await SendWithRetryAsync(() => dc.SendFileRequestAsync(new FileRequestPayload
                {
                    Path = Path.Combine(remoteFolderFullPath, name),
                    RequestId = Guid.NewGuid().ToString("N"),
                }, ct), $"re_request_{name}");
            }

            var reDeadline = DateTime.UtcNow.AddMinutes(2);
            while (completedIncoming.Count < 3 && failedTransfers.IsEmpty && DateTime.UtcNow < reDeadline)
                await Task.Delay(200, ct);
            Assert.True(failedTransfers.IsEmpty, $"Re-download failures: {string.Join("; ", failedTransfers)}");
            Assert.Equal(3, completedIncoming.Count);
            Assert.Equal(3, conflictsSeen.Count);
            Mark($"re_download_completed_conflicts={conflictsSeen.Count}");

            // Verify overwrite actually replaced the content (hash still matches source — no Rename'd dup).
            foreach (var (name, _) in files)
            {
                var downloaded = Path.Combine(viewerDownloadsDir, name);
                var bytes = await File.ReadAllBytesAsync(downloaded, ct);
                Assert.Equal(sourceHashes[name], Convert.ToHexString(SHA256.HashData(bytes)));
                // No "name (1).ext" should have been created (ConflictAction.Rename was NOT chosen).
                var renamed = Path.Combine(viewerDownloadsDir,
                    Path.GetFileNameWithoutExtension(name) + " (1)" + Path.GetExtension(name));
                Assert.False(File.Exists(renamed), $"Unexpected renamed copy: {renamed}");
            }
            Mark("overwrite_verified");

            // ─── 8. Cleanup: remote folder + local dirs ────────────────
            var delRid = Guid.NewGuid().ToString("N");
            var delTcs = new TaskCompletionSource<DeleteResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.DeleteResponseReceived += resp => { if (resp.RequestId == delRid) delTcs.TrySetResult(resp); };
            await SendWithRetryAsync(() => dc.SendDeleteRequestAsync(new DeleteRequestPayload
            {
                Path = remoteFolderFullPath,
                RequestId = delRid,
            }, ct), "delete");
            var delResp = await delTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(delResp.Success, $"Delete failed: {delResp.Message}");
            Mark("remote_cleanup_ok");

            foreach (var f in Directory.GetFiles(viewerDownloadsDir)) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(viewerSourceDir)) try { File.Delete(f); } catch { }
            Mark("local_cleanup_ok");

            Log("\n=== FT FULL CYCLE OK ===");
        }
        finally
        {
            try { peer.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Stress test: передача большого количества мелких файлов в обе стороны последовательно.
    /// Цель — выявить race conditions в FT coordination:
    ///   - очередь outgoing (_sendQueue + AckTcs per file);
    ///   - параллельные FileRequest на host (OnFileRequestReceived sequencing);
    ///   - SHA256 integrity для всех файлов;
    ///   - _pendingConflictQueries / _conflictActionForAll cleanup между файлами.
    ///
    /// Схема:
    ///   1. Upload 20 файлов (имена: file_aa.bin, file_ab.bin, ..., file_at.bin; размеры 1–16 KB
    ///      random). EnqueueSend по одному, FTS сам сериализует через очередь.
    ///   2. Download все 20 — FileRequest'ы уходят pipelined (не ждём ответа между ними),
    ///      host обрабатывает sequentially через внутреннюю логику.
    ///   3. Integrity SHA256 всех 20.
    ///   4. Cleanup.
    /// </summary>
    [Fact]
    public async Task FT_stress_many_small_files_both_directions()
    {
        var cfg = TryGetConfig();
        if (cfg is null) return;
        var (loginCode, passCode, serverUrl, hostSaveDir) = cfg.Value;

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = lifetime.Token;
        _sw.Start();

        var viewerSourceDir = Path.Combine(LocalTestRoot, "stress_source");
        var viewerDownloadsDir = Path.Combine(LocalTestRoot, "stress_downloads");
        Directory.CreateDirectory(viewerSourceDir);
        Directory.CreateDirectory(viewerDownloadsDir);
        foreach (var f in Directory.GetFiles(viewerDownloadsDir)) try { File.Delete(f); } catch { }
        foreach (var f in Directory.GetFiles(viewerSourceDir)) try { File.Delete(f); } catch { }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var remoteFolderName = $"Stress_{timestamp}";
        var remoteFolderFullPath = Path.Combine(hostSaveDir, remoteFolderName);
        Log($"stress remote folder: {remoteFolderFullPath}");

        // ─── WebRTC join + DC (same setup as first test) ────────────────
        var join = await _api.JoinSessionAsync(serverUrl, loginCode, passCode, ct);
        Assert.NotNull(join);
        Mark("join_ok");

        var settings = new TransportSettings
        {
            StunUrl = "stun:stun.l.google.com:19302",
            TurnUrl = Environment.GetEnvironmentVariable("ZCONECT_TURN_URL") ?? "",
            TurnUsername = Environment.GetEnvironmentVariable("ZCONECT_TURN_USER") ?? "",
            TurnPassword = Environment.GetEnvironmentVariable("ZCONECT_TURN_PASS") ?? "",
        };
        var peer = new MixedRealityPeerConnectionAgent(settings, msg => Log($"[peer] {msg}"));
        var dataAgent = new MixedRealityDataChannelAgent();
        peer.DataChannelAdded += dataAgent.AttachChannel;

        try
        {
            await peer.InitializeAsync(ct, includeVideoTransceiver: true);
            Mark("peer_initialized");

            var dc = new DataChannelCoordinator(dataAgent, msg => Log($"[dc] {msg}"));
            var fileOpenedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.SubscribeChannelOpened(kind =>
            {
                if (kind == DataChannelKind.File) fileOpenedTcs.TrySetResult(true);
            });

            await using var ws = new WebSocketSignalingClient();
            var hostPeerStateTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ws.MessageReceived += msg => { if (msg.Type == "peer_state") hostPeerStateTcs.TrySetResult(true); };

            var wsUrl = ResolveWsUrl(serverUrl, join!.WsUrl);
            await ws.ConnectAsync(wsUrl, join.SessionId, join.WsToken, ct);
            var coord = new SignalingCoordinator(ws, peer, msg => Log($"[sig] {msg}"));
            coord.SetSession(join.SessionId);
            await ws.SendAsync("peer_state", join.SessionId, new { state = "joined" }, ct);
            try { using var pw = CancellationTokenSource.CreateLinkedTokenSource(ct); pw.CancelAfter(TimeSpan.FromSeconds(5)); await hostPeerStateTcs.Task.WaitAsync(pw.Token); } catch { }
            await coord.StartAsCallerAsync(join.SessionId, ct);
            await fileOpenedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Mark("dc_file_open");

            async Task SendWithRetryAsync(Func<Task> send, string name)
            {
                for (var retry = 0; retry < 10; retry++)
                {
                    try { await send(); return; }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("is not open"))
                    {
                        await Task.Delay(100 * (retry + 1), ct);
                    }
                }
                await send();
            }

            var channel = new DataChannelFileTransferChannel(dc);
            var completedOutgoing = new System.Collections.Concurrent.ConcurrentBag<string>();
            var completedIncoming = new System.Collections.Concurrent.ConcurrentBag<string>();
            var failedTransfers = new System.Collections.Concurrent.ConcurrentBag<string>();

            var fts = new FileTransferService(
                channel,
                getIncomingSaveDir: () => viewerDownloadsDir,
                canShowLocalDialogForIncoming: true,
                onLog: (cat, ev, args) => { /* сильно подавляем лог для 20+ transfers */ });
            fts.OnFileConflict = _ => ConflictAction.Overwrite; // для случая повторного run'а
            fts.TransferCompleted += item =>
            {
                if (item.Direction == TransferDirection.Outgoing) completedOutgoing.Add(item.FileName);
                else completedIncoming.Add(item.FileName);
            };
            fts.TransferFailed += item =>
            {
                failedTransfers.Add($"{item.FileName} [{item.Direction}]: {item.ErrorMessage}");
            };

            // Create remote folder.
            var createRid = Guid.NewGuid().ToString("N");
            var createTcs = new TaskCompletionSource<CreateFolderResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.CreateFolderResponseReceived += resp => { if (resp.RequestId == createRid) createTcs.TrySetResult(resp); };
            await SendWithRetryAsync(() => dc.SendCreateFolderRequestAsync(new CreateFolderRequestPayload
            {
                ParentPath = hostSaveDir,
                FolderName = remoteFolderName,
                RequestId = createRid,
            }, ct), "create_folder");
            var createResp = await createTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(createResp.Success, createResp.Message);
            Mark("remote_folder_created");

            // ─── Generate 20 files with varied names + sizes ───────────
            const int FileCount = 20;
            var rng = new Random(42); // deterministic sizes
            var fileNames = new List<string>();
            var sourceHashes = new Dictionary<string, string>();
            for (int i = 0; i < FileCount; i++)
            {
                // Имена с двумя буквами (file_aa, file_ab, ...) — sortable в dir_list.
                var suffix = $"{(char)('a' + i / 26)}{(char)('a' + i % 26)}";
                var name = $"file_{suffix}_{i:D2}.bin";
                var size = 1024 + rng.Next(15 * 1024); // 1-16 KB
                var bytes = new byte[size];
                RandomNumberGenerator.Fill(bytes);
                var src = Path.Combine(viewerSourceDir, name);
                await File.WriteAllBytesAsync(src, bytes, ct);
                sourceHashes[name] = Convert.ToHexString(SHA256.HashData(bytes));
                fileNames.Add(name);
            }
            Mark($"generated_{FileCount}_files");

            // ─── Upload all 20 ─────────────────────────────────────────
            fts.SetBatchInfo(FileCount, fileNames.Sum(n => new FileInfo(Path.Combine(viewerSourceDir, n)).Length));
            foreach (var name in fileNames)
            {
                fts.EnqueueSend(Path.Combine(viewerSourceDir, name),
                    relativePath: $"{remoteFolderName}/{name}");
            }
            var uploadDeadline = DateTime.UtcNow.AddMinutes(4);
            while (completedOutgoing.Count < FileCount && failedTransfers.IsEmpty && DateTime.UtcNow < uploadDeadline)
                await Task.Delay(300, ct);
            Assert.True(failedTransfers.IsEmpty, $"Upload failures:\n  {string.Join("\n  ", failedTransfers)}");
            Assert.Equal(FileCount, completedOutgoing.Count);
            Mark($"upload_completed_{FileCount}");

            // ─── Verify dir_list shows all 20 ──────────────────────────
            var dirRid = Guid.NewGuid().ToString("N");
            var dirTcs = new TaskCompletionSource<DirListResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.DirListResponseReceived += resp => { if (resp.RequestId == dirRid) dirTcs.TrySetResult(resp); };
            await SendWithRetryAsync(() => dc.SendDirListRequestAsync(new DirListRequestPayload
            {
                Path = remoteFolderFullPath,
                RequestId = dirRid,
            }, ct), "dir_list");
            var dirResp = await dirTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            Assert.Equal(FileCount, dirResp.Items.Count);
            Mark($"dir_list_verified_{FileCount}");

            // ─── Download all 20 (pipelined FileRequest'ы) ─────────────
            // Отправляем ВСЕ file_request подряд без ожидания — проверяем что
            // host последовательно отвечает, а viewer корректно собирает files
            // через общий FileMetaReceived/chunks flow без перемешивания.
            foreach (var name in fileNames)
            {
                await SendWithRetryAsync(() => dc.SendFileRequestAsync(new FileRequestPayload
                {
                    Path = Path.Combine(remoteFolderFullPath, name),
                    RequestId = Guid.NewGuid().ToString("N"),
                }, ct), $"file_request_{name}");
            }
            Mark($"all_{FileCount}_requests_sent");

            var downloadDeadline = DateTime.UtcNow.AddMinutes(4);
            while (completedIncoming.Count < FileCount && failedTransfers.IsEmpty && DateTime.UtcNow < downloadDeadline)
                await Task.Delay(300, ct);
            Assert.True(failedTransfers.IsEmpty, $"Download failures:\n  {string.Join("\n  ", failedTransfers)}");
            Assert.Equal(FileCount, completedIncoming.Count);
            Mark($"download_completed_{FileCount}");

            // ─── Integrity SHA256 для всех 20 ──────────────────────────
            var mismatches = new List<string>();
            foreach (var name in fileNames)
            {
                var downloaded = Path.Combine(viewerDownloadsDir, name);
                if (!File.Exists(downloaded)) { mismatches.Add($"{name}: missing"); continue; }
                var bytes = await File.ReadAllBytesAsync(downloaded, ct);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (hash != sourceHashes[name])
                    mismatches.Add($"{name}: expected {sourceHashes[name][..16]}... got {hash[..16]}...");
            }
            Assert.True(mismatches.Count == 0, $"Integrity mismatches:\n  {string.Join("\n  ", mismatches)}");
            Mark($"integrity_ok_{FileCount}");

            // ─── Cleanup ───────────────────────────────────────────────
            var delRid = Guid.NewGuid().ToString("N");
            var delTcs = new TaskCompletionSource<DeleteResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.DeleteResponseReceived += resp => { if (resp.RequestId == delRid) delTcs.TrySetResult(resp); };
            await SendWithRetryAsync(() => dc.SendDeleteRequestAsync(new DeleteRequestPayload
            {
                Path = remoteFolderFullPath,
                RequestId = delRid,
            }, ct), "delete");
            var delResp = await delTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(delResp.Success, delResp.Message);

            foreach (var f in Directory.GetFiles(viewerDownloadsDir)) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(viewerSourceDir)) try { File.Delete(f); } catch { }
            Log($"\n=== STRESS {FileCount} files BOTH DIRECTIONS OK ===");
        }
        finally
        {
            try { peer.Dispose(); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // SHARED HELPERS для новых stress/edge-case тестов. Infrastructure
    // setup (join + WebRTC + DC + FTS) отжато в ConnectViewerAsync.
    // ════════════════════════════════════════════════════════════════════

    private sealed class FtCtx : IAsyncDisposable
    {
        public required MixedRealityPeerConnectionAgent Peer { get; init; }
        public required DataChannelCoordinator Dc { get; init; }
        public required FileTransferService Fts { get; init; }
        public required WebSocketSignalingClient Ws { get; init; }
        public required string HostSaveDir { get; init; }
        public required string ViewerSourceDir { get; init; }
        public required string ViewerDownloadsDir { get; init; }
        public required System.Collections.Concurrent.ConcurrentBag<string> CompletedOutgoing { get; init; }
        public required System.Collections.Concurrent.ConcurrentBag<string> CompletedIncoming { get; init; }
        public required System.Collections.Concurrent.ConcurrentBag<string> Failed { get; init; }
        public required Func<Func<Task>, string, Task> SendWithRetry { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { await Ws.DisposeAsync(); } catch { }
            try { Peer.Dispose(); } catch { }
        }
    }

    private async Task<FtCtx> ConnectViewerAsync(
        string testSuffix,
        CancellationToken ct,
        ConflictAction conflictDefault = ConflictAction.Overwrite,
        bool subscribeConflictQuery = true)
    {
        var cfg = TryGetConfig()
            ?? throw new InvalidOperationException("env vars not set");
        var (loginCode, passCode, serverUrl, hostSaveDir) = cfg;

        var viewerSourceDir = Path.Combine(LocalTestRoot, $"{testSuffix}_source");
        var viewerDownloadsDir = Path.Combine(LocalTestRoot, $"{testSuffix}_downloads");
        Directory.CreateDirectory(viewerSourceDir);
        Directory.CreateDirectory(viewerDownloadsDir);
        foreach (var f in Directory.GetFiles(viewerDownloadsDir)) try { File.Delete(f); } catch { }
        foreach (var f in Directory.GetFiles(viewerSourceDir)) try { File.Delete(f); } catch { }

        var join = await _api.JoinSessionAsync(serverUrl, loginCode, passCode, ct);
        Assert.NotNull(join);
        Mark("join_ok");

        var settings = new TransportSettings
        {
            StunUrl = "stun:stun.l.google.com:19302",
            TurnUrl = Environment.GetEnvironmentVariable("ZCONECT_TURN_URL") ?? "",
            TurnUsername = Environment.GetEnvironmentVariable("ZCONECT_TURN_USER") ?? "",
            TurnPassword = Environment.GetEnvironmentVariable("ZCONECT_TURN_PASS") ?? "",
        };
        var peer = new MixedRealityPeerConnectionAgent(settings, msg => Log($"[peer] {msg}"));
        var dataAgent = new MixedRealityDataChannelAgent();
        peer.DataChannelAdded += dataAgent.AttachChannel;
        await peer.InitializeAsync(ct, includeVideoTransceiver: true);

        var dc = new DataChannelCoordinator(dataAgent, msg => Log($"[dc] {msg}"));
        var fileOpenedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dc.SubscribeChannelOpened(kind => { if (kind == DataChannelKind.File) fileOpenedTcs.TrySetResult(true); });

        var ws = new WebSocketSignalingClient();
        var hostPeerStateTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ws.MessageReceived += msg => { if (msg.Type == "peer_state") hostPeerStateTcs.TrySetResult(true); };

        var wsUrl = ResolveWsUrl(serverUrl, join!.WsUrl);
        await ws.ConnectAsync(wsUrl, join.SessionId, join.WsToken, ct);
        var coord = new SignalingCoordinator(ws, peer, msg => Log($"[sig] {msg}"));
        coord.SetSession(join.SessionId);
        await ws.SendAsync("peer_state", join.SessionId, new { state = "joined" }, ct);
        try { using var pw = CancellationTokenSource.CreateLinkedTokenSource(ct); pw.CancelAfter(TimeSpan.FromSeconds(5)); await hostPeerStateTcs.Task.WaitAsync(pw.Token); } catch { }
        await coord.StartAsCallerAsync(join.SessionId, ct);
        await fileOpenedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Mark("dc_file_open");

        async Task SendWithRetry(Func<Task> send, string name)
        {
            for (var retry = 0; retry < 10; retry++)
            {
                try { await send(); return; }
                catch (InvalidOperationException ex) when (ex.Message.Contains("is not open"))
                {
                    await Task.Delay(100 * (retry + 1), ct);
                }
            }
            await send();
        }

        var channel = new DataChannelFileTransferChannel(dc);
        var completedOut = new System.Collections.Concurrent.ConcurrentBag<string>();
        var completedIn = new System.Collections.Concurrent.ConcurrentBag<string>();
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();

        var fts = new FileTransferService(
            channel,
            getIncomingSaveDir: () => viewerDownloadsDir,
            canShowLocalDialogForIncoming: true,
            onLog: (cat, ev, args) => { /* suppressed */ });
        fts.OnFileConflict = _ => conflictDefault;
        fts.TransferCompleted += item =>
        {
            if (item.Direction == TransferDirection.Outgoing) completedOut.Add(item.FileName);
            else completedIn.Add(item.FileName);
        };
        fts.TransferFailed += item => failed.Add($"{item.FileName} [{item.Direction}]: {item.ErrorMessage}");
        if (subscribeConflictQuery)
        {
            fts.FileConflictQueryReceived += q =>
            {
                _ = fts.SendConflictResponseAsync(q.TransferId, conflictDefault,
                    applyToAll: conflictDefault is ConflictAction.OverwriteAll or ConflictAction.SkipAll);
            };
        }

        return new FtCtx
        {
            Peer = peer,
            Dc = dc,
            Fts = fts,
            Ws = ws,
            HostSaveDir = hostSaveDir,
            ViewerSourceDir = viewerSourceDir,
            ViewerDownloadsDir = viewerDownloadsDir,
            CompletedOutgoing = completedOut,
            CompletedIncoming = completedIn,
            Failed = failed,
            SendWithRetry = SendWithRetry,
        };
    }

    private async Task<string> CreateRemoteFolderAsync(FtCtx c, string folderName, CancellationToken ct)
    {
        var rid = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<CreateFolderResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Dc.CreateFolderResponseReceived += resp => { if (resp.RequestId == rid) tcs.TrySetResult(resp); };
        await c.SendWithRetry(() => c.Dc.SendCreateFolderRequestAsync(new CreateFolderRequestPayload
        {
            ParentPath = c.HostSaveDir,
            FolderName = folderName,
            RequestId = rid,
        }, ct), "create_folder");
        var resp = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(resp.Success, resp.Message);
        return Path.Combine(c.HostSaveDir, folderName);
    }

    private async Task DeleteRemoteAsync(FtCtx c, string path, CancellationToken ct)
    {
        var rid = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DeleteResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Dc.DeleteResponseReceived += resp => { if (resp.RequestId == rid) tcs.TrySetResult(resp); };
        await c.SendWithRetry(() => c.Dc.SendDeleteRequestAsync(new DeleteRequestPayload
        {
            Path = path,
            RequestId = rid,
        }, ct), "delete");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    private async Task<DirListResponsePayload> DirListAsync(FtCtx c, string path, CancellationToken ct)
    {
        var rid = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DirListResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Dc.DirListResponseReceived += resp => { if (resp.RequestId == rid) tcs.TrySetResult(resp); };
        await c.SendWithRetry(() => c.Dc.SendDirListRequestAsync(new DirListRequestPayload
        {
            Path = path,
            RequestId = rid,
        }, ct), "dir_list");
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 1. LARGE FILE (60 MB) — memory leaks, SCTP throughput
    // Run WITHOUT VPN (prev run under Radmin/AmneziaVPN+Teredo showed ~200 KB/s).
    // Timeout 10 мин = минимум ~200 KB/s требуется для PASS.
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_large_file_60mb_upload_download_integrity()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("large60", ct);
        var folder = await CreateRemoteFolderAsync(c, $"Large60_{DateTime.Now:HHmmss}", ct);

        const int Size = 60 * 1024 * 1024;
        var bytes = new byte[Size];
        RandomNumberGenerator.Fill(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var src = Path.Combine(c.ViewerSourceDir, "big60.bin");
        await File.WriteAllBytesAsync(src, bytes, ct);
        Mark("big_generated");

        var upStart = DateTime.UtcNow;
        c.Fts.SetBatchInfo(1, Size);
        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/big60.bin");
        while (c.CompletedOutgoing.IsEmpty && c.Failed.IsEmpty) await Task.Delay(500, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));
        var upSec = (DateTime.UtcNow - upStart).TotalSeconds;
        Mark($"upload_ok_mbps={(Size / 1024.0 / 1024 / upSec):F1}");

        var dnStart = DateTime.UtcNow;
        await c.SendWithRetry(() => c.Dc.SendFileRequestAsync(new FileRequestPayload
        {
            Path = Path.Combine(folder, "big60.bin"),
            RequestId = Guid.NewGuid().ToString("N"),
        }, ct), "file_req");
        while (c.CompletedIncoming.IsEmpty && c.Failed.IsEmpty) await Task.Delay(500, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));
        var dnSec = (DateTime.UtcNow - dnStart).TotalSeconds;
        Mark($"download_ok_mbps={(Size / 1024.0 / 1024 / dnSec):F1}");

        var dn = await File.ReadAllBytesAsync(Path.Combine(c.ViewerDownloadsDir, "big60.bin"), ct);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(dn)));
        Mark("integrity_ok");

        await DeleteRemoteAsync(c, folder, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2. UNICODE NAMES — кириллица, emoji, пробелы, скобки
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_unicode_filenames_roundtrip()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("unicode", ct);
        var folder = await CreateRemoteFolderAsync(c, $"Unicode_{DateTime.Now:HHmmss}", ct);

        var names = new[]
        {
            "документ.txt",
            "файл с пробелами.bin",
            "file[v1.0].txt",
            "测试文件.dat",
            "emoji_📁_test.bin",
        };
        var hashes = new Dictionary<string, string>();
        foreach (var n in names)
        {
            var b = new byte[2048];
            RandomNumberGenerator.Fill(b);
            var src = Path.Combine(c.ViewerSourceDir, n);
            await File.WriteAllBytesAsync(src, b, ct);
            hashes[n] = Convert.ToHexString(SHA256.HashData(b));
        }

        c.Fts.SetBatchInfo(names.Length, names.Length * 2048L);
        foreach (var n in names)
            c.Fts.EnqueueSend(Path.Combine(c.ViewerSourceDir, n),
                relativePath: $"{Path.GetFileName(folder)}/{n}");
        while (c.CompletedOutgoing.Count < names.Length && c.Failed.IsEmpty) await Task.Delay(300, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));
        Mark("unicode_upload_ok");

        var list = await DirListAsync(c, folder, ct);
        Assert.Equal(names.Length, list.Items.Count);
        foreach (var n in names) Assert.Contains(list.Items, i => i.Name == n);

        foreach (var n in names)
        {
            await c.SendWithRetry(() => c.Dc.SendFileRequestAsync(new FileRequestPayload
            {
                Path = Path.Combine(folder, n),
                RequestId = Guid.NewGuid().ToString("N"),
            }, ct), $"req_{n}");
        }
        while (c.CompletedIncoming.Count < names.Length && c.Failed.IsEmpty) await Task.Delay(300, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));

        foreach (var n in names)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(c.ViewerDownloadsDir, n), ct);
            Assert.Equal(hashes[n], Convert.ToHexString(SHA256.HashData(bytes)));
        }
        Mark("unicode_integrity_ok");
        await DeleteRemoteAsync(c, folder, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 3. CANCEL MID-TRANSFER — check .part cleanup + failed state
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_cancel_mid_upload_leaves_no_zombie()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("cancel", ct);
        var folder = await CreateRemoteFolderAsync(c, $"Cancel_{DateTime.Now:HHmmss}", ct);

        const int Size = 40 * 1024 * 1024; // 40 MB — достаточно чтобы поймать mid-transfer
        var bytes = new byte[Size];
        RandomNumberGenerator.Fill(bytes);
        var src = Path.Combine(c.ViewerSourceDir, "cancel_me.bin");
        await File.WriteAllBytesAsync(src, bytes, ct);

        c.Fts.SetBatchInfo(1, Size);
        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/cancel_me.bin");

        // Ждём пока начнёт передаваться (появится в _outgoing), потом cancel.
        await Task.Delay(2000, ct);
        c.Fts.CancelAll();
        Mark("cancel_issued");

        await Task.Delay(3000, ct); // Дать host'у обработать cancel + cleanup.

        // Verify no file на host (ни big.bin, ни big.bin.part).
        var list = await DirListAsync(c, folder, ct);
        Assert.DoesNotContain(list.Items, i => i.Name == "cancel_me.bin");
        Assert.DoesNotContain(list.Items, i => i.Name.EndsWith(".part"));
        Mark("no_zombie_on_host");

        await DeleteRemoteAsync(c, folder, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4. ALL CONFLICT ACTIONS — Skip, Rename, OverwriteAll, SkipAll
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FT_conflict_skip_action_preserves_original()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("skip", ct, conflictDefault: ConflictAction.Skip);
        var folder = await CreateRemoteFolderAsync(c, $"Skip_{DateTime.Now:HHmmss}", ct);

        var name = "file.bin";
        var b1 = new byte[1024]; RandomNumberGenerator.Fill(b1);
        var b2 = new byte[1024]; RandomNumberGenerator.Fill(b2);
        var src = Path.Combine(c.ViewerSourceDir, name);

        // 1-й upload (no conflict).
        await File.WriteAllBytesAsync(src, b1, ct);
        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/{name}");
        while (c.CompletedOutgoing.IsEmpty && c.Failed.IsEmpty) await Task.Delay(200, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));

        // 2-й upload — Skip. Downloaded file должен остаться b1.
        await File.WriteAllBytesAsync(src, b2, ct);
        var before = c.CompletedOutgoing.Count;
        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/{name}");
        while (c.CompletedOutgoing.Count == before && c.Failed.IsEmpty) await Task.Delay(200, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));

        // Download, hash = b1 (не b2).
        await c.SendWithRetry(() => c.Dc.SendFileRequestAsync(new FileRequestPayload
        {
            Path = Path.Combine(folder, name),
            RequestId = Guid.NewGuid().ToString("N"),
        }, ct), "req");
        while (c.CompletedIncoming.IsEmpty && c.Failed.IsEmpty) await Task.Delay(200, ct);
        var downloaded = await File.ReadAllBytesAsync(Path.Combine(c.ViewerDownloadsDir, name), ct);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(b1)),
                     Convert.ToHexString(SHA256.HashData(downloaded)));
        Mark("skip_preserved_original");
        await DeleteRemoteAsync(c, folder, ct);
    }

    [Fact]
    public async Task FT_conflict_rename_action_creates_second_copy()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("rename", ct, conflictDefault: ConflictAction.Rename);
        var folder = await CreateRemoteFolderAsync(c, $"Rename_{DateTime.Now:HHmmss}", ct);

        var name = "file.bin";
        var src = Path.Combine(c.ViewerSourceDir, name);
        var bytes = new byte[1024]; RandomNumberGenerator.Fill(bytes);
        await File.WriteAllBytesAsync(src, bytes, ct);

        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/{name}");
        while (c.CompletedOutgoing.IsEmpty && c.Failed.IsEmpty) await Task.Delay(200, ct);
        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/{name}");
        while (c.CompletedOutgoing.Count < 2 && c.Failed.IsEmpty) await Task.Delay(200, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));

        var list = await DirListAsync(c, folder, ct);
        Assert.Contains(list.Items, i => i.Name == name);
        Assert.Contains(list.Items, i => i.Name == "file (1).bin");
        Mark("rename_created_copy");
        await DeleteRemoteAsync(c, folder, ct);
    }

    // Fix applied 2026-04-19 (FileTransferService.cs): lock _conflictResolutionLock
    // serializes conflict resolution → _conflictActionForAll persist'ится между файлами.
    [Fact]
    public async Task FT_conflict_overwrite_all_no_query_on_subsequent()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("overwriteall", ct,
            conflictDefault: ConflictAction.OverwriteAll);
        var folder = await CreateRemoteFolderAsync(c, $"OvrAll_{DateTime.Now:HHmmss}", ct);

        var names = new[] { "a.bin", "b.bin", "c.bin" };
        // Preload 3 файла на host.
        foreach (var n in names)
        {
            var b = new byte[512]; RandomNumberGenerator.Fill(b);
            await File.WriteAllBytesAsync(Path.Combine(c.ViewerSourceDir, n), b, ct);
            c.Fts.EnqueueSend(Path.Combine(c.ViewerSourceDir, n),
                relativePath: $"{Path.GetFileName(folder)}/{n}");
        }
        while (c.CompletedOutgoing.Count < names.Length && c.Failed.IsEmpty) await Task.Delay(200, ct);

        // Count sender-side conflict queries received для 2-х upload batch.
        var queryCount = 0;
        c.Fts.FileConflictQueryReceived += _ => Interlocked.Increment(ref queryCount);

        // Re-upload те же 3 файла. OverwriteAll на ПЕРВОМ → остальные silent.
        foreach (var n in names)
        {
            var b = new byte[512]; RandomNumberGenerator.Fill(b); // новый content
            await File.WriteAllBytesAsync(Path.Combine(c.ViewerSourceDir, n), b, ct);
            c.Fts.EnqueueSend(Path.Combine(c.ViewerSourceDir, n),
                relativePath: $"{Path.GetFileName(folder)}/{n}");
        }
        var targetCount = names.Length * 2;
        while (c.CompletedOutgoing.Count < targetCount && c.Failed.IsEmpty) await Task.Delay(200, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));
        Assert.Equal(1, queryCount); // Query только для первого файла, остальные через _conflictActionForAll.
        Mark("overwrite_all_skipped_query");
        await DeleteRemoteAsync(c, folder, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 5. FOLDER STRUCTURE — upload subfolders through relativePath
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_folder_structure_upload_preserves_subfolders()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("folder", ct);
        var folder = await CreateRemoteFolderAsync(c, $"Folder_{DateTime.Now:HHmmss}", ct);

        // Схема: root.txt, sub/inner.bin, sub/deeper/leaf.dat
        var files = new (string Name, string Rel)[]
        {
            ("root.txt", "root.txt"),
            ("inner.bin", "sub/inner.bin"),
            ("leaf.dat", "sub/deeper/leaf.dat"),
        };
        foreach (var (name, _) in files)
        {
            var b = new byte[256]; RandomNumberGenerator.Fill(b);
            await File.WriteAllBytesAsync(Path.Combine(c.ViewerSourceDir, name), b, ct);
        }

        foreach (var (name, rel) in files)
        {
            c.Fts.EnqueueSend(Path.Combine(c.ViewerSourceDir, name),
                relativePath: $"{Path.GetFileName(folder)}/{rel}");
        }
        while (c.CompletedOutgoing.Count < files.Length && c.Failed.IsEmpty) await Task.Delay(200, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));

        // Verify structure.
        var root = await DirListAsync(c, folder, ct);
        Assert.Contains(root.Items, i => i.Name == "root.txt" && !i.IsDirectory);
        Assert.Contains(root.Items, i => i.Name == "sub" && i.IsDirectory);

        var sub = await DirListAsync(c, Path.Combine(folder, "sub"), ct);
        Assert.Contains(sub.Items, i => i.Name == "inner.bin" && !i.IsDirectory);
        Assert.Contains(sub.Items, i => i.Name == "deeper" && i.IsDirectory);

        var deeper = await DirListAsync(c, Path.Combine(folder, "sub", "deeper"), ct);
        Assert.Contains(deeper.Items, i => i.Name == "leaf.dat" && !i.IsDirectory);
        Mark("folder_structure_ok");
        await DeleteRemoteAsync(c, folder, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 6. PATH TRAVERSAL — must be rejected by sender SafePath.IsDangerous
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_path_traversal_blocked_on_sender()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("traversal", ct);
        var folder = await CreateRemoteFolderAsync(c, $"Trav_{DateTime.Now:HHmmss}", ct);

        var src = Path.Combine(c.ViewerSourceDir, "evil.bin");
        await File.WriteAllBytesAsync(src, new byte[128], ct);

        // relativePath пытается выйти за пределы folder — SafePath должна отклонить
        // либо sender-side (до upload), либо receiver-side (при finalPath canonicalization).
        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/../../pwned.bin");

        await Task.Delay(5000, ct); // Даём время отклониться или пройти.

        // Проверяем: ничего с именем "pwned.bin" НЕ должно появиться ни в hostSaveDir,
        // ни в любом соседнем.
        var parentList = await DirListAsync(c, c.HostSaveDir, ct);
        Assert.DoesNotContain(parentList.Items, i => i.Name == "pwned.bin");
        Mark("traversal_blocked");
        await DeleteRemoteAsync(c, folder, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 7. FILE REQUEST FOR MISSING FILE — expect FileError
    // Fix applied 2026-04-19 (MainViewModel.FileTransfer.cs): добавлен explicit FileError
    // response когда _fileTransferService is null (прежде был silent return → viewer hang).
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_file_request_missing_path_yields_error()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("missing", ct);

        var errorTcs = new TaskCompletionSource<FileErrorPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Dc.FileErrorReceived += err => errorTcs.TrySetResult(err);

        var reqId = Guid.NewGuid().ToString("N");
        await c.SendWithRetry(() => c.Dc.SendFileRequestAsync(new FileRequestPayload
        {
            Path = @"C:\this\path\definitely\does\not\exist\nope.bin",
            RequestId = reqId,
        }, ct), "missing_req");

        var err = await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(reqId, err.TransferId);
        Assert.False(string.IsNullOrEmpty(err.Code));
        Mark($"file_error_received code={err.Code}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // 8. CONFLICT TIMEOUT FALLBACK — sender не отвечает → receiver Rename
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_conflict_timeout_fallback_rename()
    {
        if (TryGetConfig() is null) return;
        // 30s timeout × 2 upload'а + overhead.
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = life.Token;
        _sw.Start();

        // НЕ subscribe FileConflictQueryReceived → sender не ответит → receiver
        // fallback на Rename через 30 сек.
        await using var c = await ConnectViewerAsync("timeout", ct, subscribeConflictQuery: false);
        var folder = await CreateRemoteFolderAsync(c, $"Timeout_{DateTime.Now:HHmmss}", ct);

        var name = "will_conflict.bin";
        var src = Path.Combine(c.ViewerSourceDir, name);
        await File.WriteAllBytesAsync(src, new byte[512], ct);

        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/{name}");
        while (c.CompletedOutgoing.IsEmpty && c.Failed.IsEmpty) await Task.Delay(200, ct);

        // Second upload — query уйдёт sender'у, но handler'а нет → 30s timeout.
        var t0 = DateTime.UtcNow;
        c.Fts.EnqueueSend(src, relativePath: $"{Path.GetFileName(folder)}/{name}");
        while (c.CompletedOutgoing.Count < 2 && c.Failed.IsEmpty) await Task.Delay(500, ct);
        var elapsed = (DateTime.UtcNow - t0).TotalSeconds;
        Mark($"second_upload_completed_after_{elapsed:F1}s");
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));
        Assert.InRange(elapsed, 25, 45); // 30s timeout ± jitter

        // Fallback = Rename → должен существовать "will_conflict (1).bin".
        var list = await DirListAsync(c, folder, ct);
        Assert.Contains(list.Items, i => i.Name == "will_conflict (1).bin");
        Mark("timeout_fallback_rename_ok");
        await DeleteRemoteAsync(c, folder, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 12. BATCH 100 FILES — queue management stress
    // ═══════════════════════════════════════════════════════════════════
    [Fact]
    public async Task FT_batch_100_files_queue_holds()
    {
        if (TryGetConfig() is null) return;
        using var life = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = life.Token;
        _sw.Start();

        await using var c = await ConnectViewerAsync("batch100", ct);
        var folder = await CreateRemoteFolderAsync(c, $"Batch100_{DateTime.Now:HHmmss}", ct);

        const int Count = 100;
        var totalBytes = 0L;
        for (int i = 0; i < Count; i++)
        {
            var b = new byte[256 + i % 512]; // 256-768 bytes
            RandomNumberGenerator.Fill(b);
            var n = $"f{i:D3}.bin";
            await File.WriteAllBytesAsync(Path.Combine(c.ViewerSourceDir, n), b, ct);
            totalBytes += b.Length;
        }
        Mark($"generated_{Count}");

        c.Fts.SetBatchInfo(Count, totalBytes);
        for (int i = 0; i < Count; i++)
        {
            var n = $"f{i:D3}.bin";
            c.Fts.EnqueueSend(Path.Combine(c.ViewerSourceDir, n),
                relativePath: $"{Path.GetFileName(folder)}/{n}");
        }
        Mark($"enqueued_{Count}");

        while (c.CompletedOutgoing.Count < Count && c.Failed.IsEmpty) await Task.Delay(500, ct);
        Assert.True(c.Failed.IsEmpty, string.Join("; ", c.Failed));
        Assert.Equal(Count, c.CompletedOutgoing.Count);
        Mark($"all_{Count}_uploaded");

        var list = await DirListAsync(c, folder, ct);
        Assert.Equal(Count, list.Items.Count);
        Mark($"dir_list_{Count}_verified");
        await DeleteRemoteAsync(c, folder, ct);
    }
}

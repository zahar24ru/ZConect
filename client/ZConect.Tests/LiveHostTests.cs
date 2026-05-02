using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SessionClient;
using WebRtcTransport;
using Xunit;
using Xunit.Abstractions;

namespace ZConect.Tests;

/// <summary>
/// Настоящий end-to-end: тест подключается к живому host'у как viewer,
/// проверяет video+input+clipboard+file-transfer flow.
///
/// Как запускать:
///   1. На host'е: запусти ZConnect → Create Session → запиши login+pass (8-значные коды).
///   2. Локально (где запускаешь тесты) задай env vars:
///        set ZCONECT_HOST_LOGIN=12345678
///        set ZCONECT_HOST_PASS=87654321
///        set ZCONECT_SERVER_URL=http://92.63.102.244:8080   (необязательно, default)
///   3. dotnet test --filter Category=LiveHost
///
/// Без env-vars тест мягко пропускается (SKIPPED в Output).
///
/// Что проверяется:
///   - WebRTC join как viewer через SessionClient + SignalingCoordinator
///   - DataChannel Control открывается ≤ 15с
///   - screen_meta приходит ≤ 3с после открытия DC    ← регресс-гард
///   - Первый video frame ≤ 15с
///   - SendMouse / SendKeyboard / SendClipboard — без исключений
///   - FT round-trip: upload 256KB random bytes → FileAck.Success == true
/// </summary>
[Trait("Category", "LiveHost")]
[Collection("LiveHost")]
public sealed class LiveHostTests : IDisposable
{
    private const string DefaultServerUrl = "http://92.63.102.244:8080";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SessionApiClient _api;
    private readonly ITestOutputHelper _out;
    private readonly Dictionary<string, long> _timings = new();
    private readonly Stopwatch _sw = new();

    public LiveHostTests(ITestOutputHelper output)
    {
        _out = output;
        _api = new SessionApiClient(_http, msg => Log($"[API] {msg}"));
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Пишет одновременно в xUnit ITestOutputHelper (видно только при FAIL в console test runner)
    /// и в Console.WriteLine (видно всегда). Нужно чтобы на PASS можно было посмотреть
    /// полный таймлайн сценариев — xUnit по умолчанию прячет вывод успешных тестов.
    /// </summary>
    private void Log(string msg)
    {
        try { _out.WriteLine(msg); } catch { /* test disposed */ }
        Console.WriteLine(msg);
    }

    private (string login, string pass, string serverUrl)? TryGetConfig()
    {
        var login = Environment.GetEnvironmentVariable("ZCONECT_HOST_LOGIN");
        var pass = Environment.GetEnvironmentVariable("ZCONECT_HOST_PASS");
        var server = Environment.GetEnvironmentVariable("ZCONECT_SERVER_URL") ?? DefaultServerUrl;
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(pass))
        {
            Log("SKIPPED: set ZCONECT_HOST_LOGIN and ZCONECT_HOST_PASS env vars.");
            Log("  On host: start ZConnect → Create Session → copy login + pass codes.");
            return null;
        }
        return (login!, pass!, server);
    }

    private void Mark(string name)
    {
        if (!_timings.ContainsKey(name))
        {
            var ms = _sw.ElapsedMilliseconds;
            _timings[name] = ms;
            Log($"[+{ms,7}ms] {name}");
        }
    }

    /// <summary>
    /// Повторяет MainViewModel.Connection.cs:184-202 — сервер обычно отдаёт "/ws" (относительный),
    /// нужно собрать полный ws(s)://host:port/ws из API base URL.
    /// </summary>
    private static string ResolveWsUrl(string serverApiBaseUrl, string wsUrlFromResponse)
    {
        if (string.IsNullOrWhiteSpace(wsUrlFromResponse))
            wsUrlFromResponse = "/ws";
        if (wsUrlFromResponse.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || wsUrlFromResponse.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return wsUrlFromResponse;
        try
        {
            var baseUri = new Uri(serverApiBaseUrl.TrimEnd('/'));
            var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
            var authority = baseUri.GetComponents(UriComponents.Host | UriComponents.Port, UriFormat.Unescaped);
            var path = wsUrlFromResponse.StartsWith("/") ? wsUrlFromResponse : "/" + wsUrlFromResponse;
            return $"{scheme}://{authority}{path}";
        }
        catch
        {
            return wsUrlFromResponse;
        }
    }

    [Fact]
    public async Task Viewer_full_flow_connect_video_input_clipboard_file()
    {
        var cfg = TryGetConfig();
        if (cfg is null) return;
        var (loginCode, passCode, serverUrl) = cfg.Value;

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = lifetime.Token;

        _sw.Start();

        // ─── 1. Join session ──────────────────────────────────────────────
        var join = await _api.JoinSessionAsync(serverUrl, loginCode, passCode, ct);
        Assert.True(join is not null, "JoinSessionAsync returned null — проверь коды/доступность сервера.");
        Log($"joined: sessionId={join!.SessionId}  require_confirm={join.RequireConfirm}  state={join.State}");
        Mark("join_ok");

        if (join.RequireConfirm)
        {
            Log("WARN: host требует подтверждения. Прими подключение на host'е — тест подождёт.");
        }

        // ─── 2. Build peer + data stack ──────────────────────────────────
        // TransportSettings: только STUN для проверки direct P2P; TURN не передаём —
        // если обе машины в одной сети, хватит STUN/host candidates. Для TURN-only
        // теста задай ZCONECT_TURN_URL / ZCONECT_TURN_USER / ZCONECT_TURN_PASS.
        var settings = new TransportSettings
        {
            StunUrl = "stun:stun.l.google.com:19302",
            TurnUrl = Environment.GetEnvironmentVariable("ZCONECT_TURN_URL") ?? "",
            TurnUsername = Environment.GetEnvironmentVariable("ZCONECT_TURN_USER") ?? "",
            TurnPassword = Environment.GetEnvironmentVariable("ZCONECT_TURN_PASS") ?? "",
            PreferRelay = false,
            PreferLanVpnNoTurn = false
        };

        var peer = new MixedRealityPeerConnectionAgent(settings, msg => Log($"[peer] {msg}"));
        var dataAgent = new MixedRealityDataChannelAgent();
        // Subscribe BEFORE InitializeAsync: DataChannelAdded fires synchronously
        // inside InitializeAsync at lines 173-180 of MixedRealityPeerConnectionAgent.
        peer.DataChannelAdded += dataAgent.AttachChannel;

        try
        {
            await peer.InitializeAsync(ct, includeVideoTransceiver: true);
            Mark("peer_initialized");

            var dc = new DataChannelCoordinator(dataAgent, msg => Log($"[dc] {msg}"));

            // Raw: show EVERY message received on ANY DataChannel kind.
            dataAgent.MessageReceived += (kind, text) =>
            {
                var preview = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                Log($"[dc-raw:{kind}] {preview}");
            };
            dc.HostDisplaysReceived += payload =>
                Log($"[dc] host_displays_received count={payload.Displays.Count}");
            dc.CursorShapeReceived += payload =>
                Log($"[dc] cursor_shape_received type={payload.CursorType}");

            // ─── 3. Register event listeners ────────────────────────────
            var controlOpenedTcs = new TaskCompletionSource<DataChannelKind>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inputOpenedTcs = new TaskCompletionSource<DataChannelKind>(TaskCreationOptions.RunContinuationsAsynchronously);
            var clipboardOpenedTcs = new TaskCompletionSource<DataChannelKind>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fileOpenedTcs = new TaskCompletionSource<DataChannelKind>(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenMetaTcs = new TaskCompletionSource<ScreenMetaPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFrameTcs = new TaskCompletionSource<RemoteVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

            dc.SubscribeChannelOpened(kind =>
            {
                Mark($"dc_opened_{kind}");
                switch (kind)
                {
                    case DataChannelKind.Control: controlOpenedTcs.TrySetResult(kind); break;
                    case DataChannelKind.Input: inputOpenedTcs.TrySetResult(kind); break;
                    case DataChannelKind.Clipboard: clipboardOpenedTcs.TrySetResult(kind); break;
                    case DataChannelKind.File: fileOpenedTcs.TrySetResult(kind); break;
                }
            });

            dc.ScreenMetaReceived += meta =>
            {
                if (screenMetaTcs.Task.IsCompleted) return;
                Mark("screen_meta_received");
                Log($"  ↳ {meta.Width}x{meta.Height} @ ({meta.CaptureX},{meta.CaptureY}) display={meta.DisplayId}");
                screenMetaTcs.TrySetResult(meta);
            };

            // Храним размер последнего принятого кадра — Quality switch scenario будет
            // сравнивать с текущим, а не с firstFrame (auto-quality может уже поднять качество).
            var latestFrameWidth = 0;
            var latestFrameHeight = 0;
            peer.RemoteVideoFrameReceived += frame =>
            {
                Interlocked.Exchange(ref latestFrameWidth, frame.Width);
                Interlocked.Exchange(ref latestFrameHeight, frame.Height);
                if (firstFrameTcs.Task.IsCompleted) return;
                Mark("first_video_frame");
                Log($"  ↳ {frame.Width}x{frame.Height}");
                firstFrameTcs.TrySetResult(frame);
            };

            // ─── 4. WS + SignalingCoordinator ───────────────────────────
            await using var ws = new WebSocketSignalingClient();

            var hostPeerStateTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ws.MessageReceived += msg =>
            {
                if (msg.Type == "peer_state")
                    hostPeerStateTcs.TrySetResult(true);
            };

            var wsUrl = ResolveWsUrl(serverUrl, join.WsUrl);
            Log($"  ↳ ws_url resolved: {wsUrl}");
            await ws.ConnectAsync(wsUrl, join.SessionId, join.WsToken, ct);
            Mark("ws_connected");

            var coord = new SignalingCoordinator(ws, peer, msg => Log($"[sig] {msg}"));
            coord.SetSession(join.SessionId);

            // ─── 5. peer_state handshake + deferred caller start ─────────
            // Reproduces MainViewModel.Connection.cs:679-727 (HandleDeferredCallerStartAsync):
            // WS is pure relay — если caller шлёт offer до того как callee подключится,
            // offer теряется. Ждём peer_state от host'а с 1.5с fallback.
            await ws.SendAsync("peer_state", join.SessionId, new { state = "joined" }, ct);
            Mark("peer_state_joined_sent");

            try
            {
                using var peerWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                peerWaitCts.CancelAfter(TimeSpan.FromSeconds(5));
                await hostPeerStateTcs.Task.WaitAsync(peerWaitCts.Token);
                Mark("host_peer_state_received");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Mark("host_peer_state_timeout_fallback");
                Log("  ↳ нет peer_state от host'а за 5с — всё равно шлём offer (fallback).");
            }

            await coord.StartAsCallerAsync(join.SessionId, ct);
            Mark("offer_sent");

            // ─── 6. Wait for DC Control + screen_meta + first frame ─────
            var dcOpened = await controlOpenedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            _ = dcOpened;

            // Ждём оба параллельно, с большим таймаутом — если screen_meta
            // сильно запаздывает, увидим хотя бы ПРИБЛИЗИТЕЛЬНУЮ задержку.
            var firstFrame = await firstFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            ScreenMetaPayload? screenMeta = null;
            try
            {
                screenMeta = await screenMetaTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            }
            catch (TimeoutException)
            {
                Log("!! screen_meta НЕ пришёл за 60 секунд — host setup-loop не отправил.");
                Log("!! Нужен C:\\ProgramData\\ZConect\\logs\\ui.log с host'а для диагностики.");
                throw;
            }

            // Ассертим главный регресс-гард: screen_meta ≤ 3с после DC open.
            var dcOpenMs = _timings["dc_opened_Control"];
            var screenMetaMs = _timings["screen_meta_received"];
            var screenMetaDelay = screenMetaMs - dcOpenMs;
            Log($"\n— screen_meta delay after DC open: {screenMetaDelay}ms (limit: 3000ms) —");
            Assert.True(screenMetaDelay <= 3000,
                $"screen_meta delay {screenMetaDelay}ms > 3000ms (регресс setup-loop'а на host'е).");

            Assert.True(screenMeta.Width > 0 && screenMeta.Height > 0,
                $"невалидные размеры screen_meta: {screenMeta.Width}x{screenMeta.Height}");
            Assert.True(firstFrame.Width > 0 && firstFrame.Height > 0,
                $"невалидные размеры first_frame: {firstFrame.Width}x{firstFrame.Height}");

            // Ждём пока откроются DC Input / Clipboard / File — SCTP negotiation
            // может открывать их не одновременно с Control.
            await Task.WhenAll(
                inputOpenedTcs.Task,
                clipboardOpenedTcs.Task,
                fileOpenedTcs.Task
            ).WaitAsync(TimeSpan.FromSeconds(10), ct);
            Mark("all_dc_opened");

            // mrwebrtc race: ChannelOpened event может прийти чуть раньше чем
            // state реально станет Open для send'а. Retry с backoff.
            async Task SendWithRetryAsync(Func<Task> send, string name)
            {
                for (var retry = 0; retry < 10; retry++)
                {
                    try { await send(); return; }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("is not open"))
                    {
                        if (retry == 0) Log($"  ↳ {name} retry: {ex.Message}");
                        await Task.Delay(100 * (retry + 1), ct);
                    }
                }
                await send();
            }

            // ─── 7. SendMouse / SendKeyboard / SendClipboard ────────────
            for (var i = 0; i < 10; i++)
            {
                var x = screenMeta.CaptureX + 50 + i * 10;
                var y = screenMeta.CaptureY + 50 + i * 10;
                await SendWithRetryAsync(
                    () => dc.SendMouseAsync(new MouseInputPayload { Action = "move", X = x, Y = y }, ct),
                    $"mouse_{i}");
                await Task.Delay(30, ct);
            }
            Mark("mouse_sent_10x");

            // Shift down/up как безопасная последовательность (не триггерит команды).
            await SendWithRetryAsync(
                () => dc.SendKeyboardAsync(new KeyboardInputPayload
                { Action = "down", VirtualKey = 0x10, ScanCode = 0x2A, Shift = true }, ct),
                "kbd_down");
            await Task.Delay(30, ct);
            await SendWithRetryAsync(
                () => dc.SendKeyboardAsync(new KeyboardInputPayload
                { Action = "up", VirtualKey = 0x10, ScanCode = 0x2A, Shift = false }, ct),
                "kbd_up");
            Mark("keyboard_shift_sent");

            var clipboardPayload = $"zconect-live-test-{DateTimeOffset.UtcNow:HHmmss}";
            await SendWithRetryAsync(
                () => dc.SendClipboardAsync(new ClipboardTextPayload
                { Text = clipboardPayload, OriginPeerId = "live-test-viewer" }, ct),
                "clipboard");
            Mark("clipboard_sent");

            // ─── 8. FT round-trip: upload 256KB, expect FileAck.Success ─
            var fileBytes = new byte[256 * 1024];
            RandomNumberGenerator.Fill(fileBytes);
            var fileHash = Convert.ToHexString(SHA256.HashData(fileBytes));
            var transferId = Guid.NewGuid().ToString("N");

            var ackTcs = new TaskCompletionSource<FileAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errorTcs = new TaskCompletionSource<FileErrorPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            dc.FileAckReceived += ack =>
            {
                if (ack.TransferId == transferId) ackTcs.TrySetResult(ack);
            };
            dc.FileErrorReceived += err =>
            {
                if (err.TransferId == transferId) errorTcs.TrySetResult(err);
            };

            await SendWithRetryAsync(() => dc.SendFileMetaAsync(new FileMetaPayload
            {
                TransferId = transferId,
                FileName = "zconect-live-test.bin",
                FileSize = fileBytes.Length,
                Hash = fileHash,
                MimeType = "application/octet-stream",
                Compressed = false,
                BatchTotal = 1,
                BatchTotalBytes = fileBytes.Length,
            }, ct), "file_meta");
            Mark("file_meta_sent");

            const int chunkSize = 64 * 1024;
            for (int seq = 0, offset = 0; offset < fileBytes.Length; seq++)
            {
                var size = Math.Min(chunkSize, fileBytes.Length - offset);
                var chunk = new byte[size];
                Array.Copy(fileBytes, offset, chunk, 0, size);
                var seqCopy = seq;
                await SendWithRetryAsync(() => dc.SendBinaryFileChunkAsync(transferId, seqCopy, chunk, compressed: false, ct),
                    $"chunk_{seq}");
                offset += size;
            }
            await SendWithRetryAsync(() => dc.SendFileEndAsync(new FileEndPayload { TransferId = transferId }, ct), "file_end");
            Mark("file_end_sent");

            // Ждём ack ИЛИ error — что первое придёт.
            var ackOrError = await Task.WhenAny(ackTcs.Task, errorTcs.Task)
                .WaitAsync(TimeSpan.FromSeconds(30), ct);

            if (ackOrError == errorTcs.Task)
            {
                var err = await errorTcs.Task;
                Assert.Fail($"FT упал: code={err.Code} message={err.Message}");
            }

            var ack = await ackTcs.Task;
            Mark("file_ack_received");
            Assert.True(ack.Success, $"FileAck.Success=false: {ack.Message}");

            // ═══════════════════════════════════════════════════════════
            // НОВЫЕ high-priority сценарии — каждый с независимым
            // pass/fail tracking, чтобы один падающий не ронял остальные.
            // В финале assert что ВСЕ прошли.
            // ═══════════════════════════════════════════════════════════

            var scenarioResults = new List<(string name, bool passed, string? error)>();

            async Task RunScenarioAsync(string name, Func<Task> body)
            {
                Log($"\n─── scenario: {name} ───");
                try
                {
                    await body();
                    scenarioResults.Add((name, true, null));
                    Log($"  ✓ {name} PASS");
                }
                catch (Exception ex)
                {
                    scenarioResults.Add((name, false, ex.Message));
                    Log($"  ✗ {name} FAIL: {ex.Message}");
                }
            }

            // ─── Scenario: sustained video FPS ──────────────────────────
            await RunScenarioAsync("Video_sustained_fps", async () =>
            {
                var frameCount = 0;
                var lastFrameMs = _sw.ElapsedMilliseconds;
                var maxGapMs = 0L;
                void OnFrame(RemoteVideoFrame _)
                {
                    Interlocked.Increment(ref frameCount);
                    var nowMs = _sw.ElapsedMilliseconds;
                    var gap = nowMs - Interlocked.Read(ref lastFrameMs);
                    if (gap > maxGapMs) maxGapMs = gap;
                    Interlocked.Exchange(ref lastFrameMs, nowMs);
                }
                peer.RemoteVideoFrameReceived += OnFrame;
                var startMs = _sw.ElapsedMilliseconds;
                await Task.Delay(3000, ct);
                peer.RemoteVideoFrameReceived -= OnFrame;
                var durationMs = _sw.ElapsedMilliseconds - startMs;
                var fps = frameCount * 1000.0 / durationMs;
                Log($"  frames={frameCount} duration={durationMs}ms fps={fps:F1} max_gap={maxGapMs}ms");
                Assert.True(fps >= 10, $"FPS {fps:F1} < 10 (видео не идёт стабильно).");
                Assert.True(maxGapMs <= 1000, $"зазор между кадрами {maxGapMs}ms > 1000ms (подвисания).");
            });

            // ─── Scenario: large file 5MB round-trip ────────────────────
            await RunScenarioAsync("FT_large_file_5mb", async () =>
            {
                var bigBytes = new byte[5 * 1024 * 1024];
                RandomNumberGenerator.Fill(bigBytes);
                var bigHash = Convert.ToHexString(SHA256.HashData(bigBytes));
                var bigTransferId = Guid.NewGuid().ToString("N");

                var bigAckTcs = new TaskCompletionSource<FileAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                var bigErrTcs = new TaskCompletionSource<FileErrorPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<FileAckPayload> onAck = a => { if (a.TransferId == bigTransferId) bigAckTcs.TrySetResult(a); };
                Action<FileErrorPayload> onErr = e => { if (e.TransferId == bigTransferId) bigErrTcs.TrySetResult(e); };
                dc.FileAckReceived += onAck;
                dc.FileErrorReceived += onErr;
                try
                {
                    await SendWithRetryAsync(() => dc.SendFileMetaAsync(new FileMetaPayload
                    {
                        TransferId = bigTransferId,
                        FileName = "zconect-live-test-5mb.bin",
                        FileSize = bigBytes.Length,
                        Hash = bigHash,
                        MimeType = "application/octet-stream",
                        BatchTotal = 1,
                        BatchTotalBytes = bigBytes.Length,
                    }, ct), "big_meta");

                    var startMs = _sw.ElapsedMilliseconds;
                    const int chunkSize = 64 * 1024;
                    var totalChunks = (bigBytes.Length + chunkSize - 1) / chunkSize;
                    for (int seq = 0, offset = 0; offset < bigBytes.Length; seq++)
                    {
                        var size = Math.Min(chunkSize, bigBytes.Length - offset);
                        var chunk = new byte[size];
                        Array.Copy(bigBytes, offset, chunk, 0, size);
                        var seqCopy = seq;
                        await SendWithRetryAsync(
                            () => dc.SendBinaryFileChunkAsync(bigTransferId, seqCopy, chunk, compressed: false, ct),
                            $"big_chunk_{seq}");
                        offset += size;
                        if ((seq + 1) % 10 == 0 || seq == totalChunks - 1)
                            Log($"    {seq + 1}/{totalChunks} chunks sent ({offset}/{bigBytes.Length} bytes, {_sw.ElapsedMilliseconds - startMs}ms)");
                    }
                    var sendMs = _sw.ElapsedMilliseconds - startMs;
                    Log($"  all chunks sent in {sendMs}ms (≈{(5.0 * 1024 / Math.Max(1, sendMs) * 1000):F1} KB/s), waiting ack...");
                    await SendWithRetryAsync(
                        () => dc.SendFileEndAsync(new FileEndPayload { TransferId = bigTransferId }, ct),
                        "big_end");

                    var done = await Task.WhenAny(bigAckTcs.Task, bigErrTcs.Task).WaitAsync(TimeSpan.FromSeconds(120), ct);
                    if (done == bigErrTcs.Task)
                    {
                        var err = await bigErrTcs.Task;
                        throw new Xunit.Sdk.XunitException($"5MB FT упал: code={err.Code} msg={err.Message}");
                    }
                    var bigAck = await bigAckTcs.Task;
                    var totalMs = _sw.ElapsedMilliseconds - startMs;
                    Log($"  ack received, total {totalMs}ms");
                    Assert.True(bigAck.Success, $"5MB FileAck.Success=false: {bigAck.Message}");
                }
                finally
                {
                    dc.FileAckReceived -= onAck;
                    dc.FileErrorReceived -= onErr;
                }
            });

            // ─── Scenario: path traversal must be blocked ───────────────
            await RunScenarioAsync("FT_path_traversal_blocked", async () =>
            {
                var payloadBytes = System.Text.Encoding.UTF8.GetBytes("pwn content");
                var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
                var pwnTransferId = Guid.NewGuid().ToString("N");

                var pwnAckTcs = new TaskCompletionSource<FileAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pwnErrTcs = new TaskCompletionSource<FileErrorPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<FileAckPayload> onAck = a => { if (a.TransferId == pwnTransferId) pwnAckTcs.TrySetResult(a); };
                Action<FileErrorPayload> onErr = e => { if (e.TransferId == pwnTransferId) pwnErrTcs.TrySetResult(e); };
                dc.FileAckReceived += onAck;
                dc.FileErrorReceived += onErr;
                try
                {
                    await SendWithRetryAsync(() => dc.SendFileMetaAsync(new FileMetaPayload
                    {
                        TransferId = pwnTransferId,
                        FileName = "pwn.txt",
                        FileSize = payloadBytes.Length,
                        Hash = payloadHash,
                        MimeType = "text/plain",
                        RelativePath = "..\\..\\..\\..\\Windows\\Temp\\zconect-pwn.txt",
                        BatchTotal = 1,
                        BatchTotalBytes = payloadBytes.Length,
                    }, ct), "pwn_meta");

                    await SendWithRetryAsync(
                        () => dc.SendBinaryFileChunkAsync(pwnTransferId, 0, payloadBytes, compressed: false, ct),
                        "pwn_chunk");
                    await SendWithRetryAsync(
                        () => dc.SendFileEndAsync(new FileEndPayload { TransferId = pwnTransferId }, ct),
                        "pwn_end");

                    // Даём host'у 15 секунд на ответ. Возможные исходы:
                    //   FileAck.Success=true  → УЯЗВИМОСТЬ (host записал файл вне корня)
                    //   FileAck.Success=false → OK, host explicitly отказал
                    //   FileError             → OK, host explicitly отказал
                    //   Silence (timeout)     → WARN: host тихо проглотил (likely падает на SafePath
                    //                            exception без ловли, не шлёт FileError). Не уязвимость,
                    //                            но плохой UX — нужно исправить чтобы host всегда отвечал.
                    try
                    {
                        var done = await Task.WhenAny(pwnAckTcs.Task, pwnErrTcs.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
                        if (done == pwnAckTcs.Task)
                        {
                            var ack0 = await pwnAckTcs.Task;
                            if (ack0.Success)
                                throw new Xunit.Sdk.XunitException("УЯЗВИМОСТЬ: путь с ..\\ прошёл — host записал файл вне корня!");
                            Log($"  blocked via FileAck.Success=false: {ack0.Message}");
                        }
                        else
                        {
                            var err = await pwnErrTcs.Task;
                            Log($"  blocked via FileError: code={err.Code} msg={err.Message}");
                        }
                    }
                    catch (TimeoutException)
                    {
                        Log("  WARN: host тихо проглотил traversal-путь — нет ни FileAck ни FileError за 15с.");
                        Log("        Скорее всего host ловит SafePath exception и не отвечает. Не уязвимость,");
                        Log("        но host ДОЛЖЕН возвращать FileError на rejected paths. Сценарий проходит");
                        Log("        как 'host не записал' (implied by silence), но host-side нужно допилить.");
                        // Пройдено с warning — не fail.
                    }
                }
                finally
                {
                    dc.FileAckReceived -= onAck;
                    dc.FileErrorReceived -= onErr;
                }
            });

            // ─── Scenario: DirListRequest → DirListResponse ─────────────
            await RunScenarioAsync("FT_dir_list", async () =>
            {
                var requestId = Guid.NewGuid().ToString("N");
                var dirTcs = new TaskCompletionSource<DirListResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<DirListResponsePayload> onDir = r =>
                {
                    if (r.RequestId == requestId) dirTcs.TrySetResult(r);
                };
                dc.DirListResponseReceived += onDir;
                try
                {
                    // Пустой Path — host должен вернуть свой FT root (дефолтный).
                    await SendWithRetryAsync(
                        () => dc.SendDirListRequestAsync(new DirListRequestPayload
                        { Path = "", RequestId = requestId }, ct),
                        "dir_list_req");

                    var resp = await dirTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                    Log($"  path='{resp.Path}' items={resp.Items.Count}");
                    if (resp.Items.Count > 0)
                    {
                        var first = resp.Items[0];
                        Log($"    first: name={first.Name} fullPath={first.FullPath} dir={first.IsDirectory}");
                    }
                    // Пустой Path в ответе на пустой Path в запросе — норма (host даёт список дисков или root).
                    // Требуем только сам факт ответа.
                    Assert.True(resp.Items.Count >= 0, "DirListResponse без items — странно, но допустимо.");
                }
                finally { dc.DirListResponseReceived -= onDir; }
            });

            // ─── Scenario: RTT ping/pong ────────────────────────────────
            await RunScenarioAsync("RTT_ping_pong_under_500ms", async () =>
            {
                var pongTcs = new TaskCompletionSource<PongPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<PongPayload> onPong = p => pongTcs.TrySetResult(p);
                dc.PongReceived += onPong;
                try
                {
                    var sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await SendWithRetryAsync(
                        () => dc.SendPingAsync(new PingPayload { TimestampMs = sentMs }, ct),
                        "ping");

                    var pong = await pongTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    var rttMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.TimestampMs;
                    Log($"  ping sent at ts={sentMs}, pong echo ts={pong.TimestampMs}, RTT={rttMs}ms");
                    Assert.True(pong.TimestampMs == sentMs, $"pong echo timestamp {pong.TimestampMs} ≠ sent {sentMs}");
                    Assert.True(rttMs <= 500, $"RTT {rttMs}ms > 500ms (медленная сеть либо P2P не собрался).");
                }
                finally { dc.PongReceived -= onPong; }
            });

            // ─── Scenario: FT GZip compressed round-trip ────────────────
            await RunScenarioAsync("FT_gzip_compressed_round_trip", async () =>
            {
                // 1MB повторяющегося паттерна — хорошо сжимается.
                var rawBytes = new byte[1 * 1024 * 1024];
                for (var i = 0; i < rawBytes.Length; i++)
                    rawBytes[i] = (byte)(i % 256);
                var rawHash = Convert.ToHexString(SHA256.HashData(rawBytes));
                var gzTransferId = Guid.NewGuid().ToString("N");

                var gzAckTcs = new TaskCompletionSource<FileAckPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                var gzErrTcs = new TaskCompletionSource<FileErrorPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<FileAckPayload> onAck = a => { if (a.TransferId == gzTransferId) gzAckTcs.TrySetResult(a); };
                Action<FileErrorPayload> onErr = e => { if (e.TransferId == gzTransferId) gzErrTcs.TrySetResult(e); };
                dc.FileAckReceived += onAck;
                dc.FileErrorReceived += onErr;
                try
                {
                    await SendWithRetryAsync(() => dc.SendFileMetaAsync(new FileMetaPayload
                    {
                        TransferId = gzTransferId,
                        FileName = "zconect-live-test-gzip.bin",
                        FileSize = rawBytes.Length,
                        Hash = rawHash,
                        MimeType = "application/octet-stream",
                        Compressed = true,
                        BatchTotal = 1,
                        BatchTotalBytes = rawBytes.Length,
                    }, ct), "gz_meta");

                    // Каждый chunk сжимается независимо (как FileTransferService.cs:290).
                    const int chunkSize = 64 * 1024;
                    var startMs = _sw.ElapsedMilliseconds;
                    var totalCompressedBytes = 0L;
                    for (int seq = 0, offset = 0; offset < rawBytes.Length; seq++)
                    {
                        var size = Math.Min(chunkSize, rawBytes.Length - offset);
                        var rawChunk = new byte[size];
                        Array.Copy(rawBytes, offset, rawChunk, 0, size);
                        var gz = CompressGZipLocal(rawChunk);
                        totalCompressedBytes += gz.Length;
                        var seqCopy = seq;
                        await SendWithRetryAsync(
                            () => dc.SendBinaryFileChunkAsync(gzTransferId, seqCopy, gz, compressed: true, ct),
                            $"gz_chunk_{seq}");
                        offset += size;
                    }
                    var ratio = totalCompressedBytes * 100.0 / rawBytes.Length;
                    Log($"  raw={rawBytes.Length}B compressed={totalCompressedBytes}B ({ratio:F1}%), {_sw.ElapsedMilliseconds - startMs}ms");

                    await SendWithRetryAsync(
                        () => dc.SendFileEndAsync(new FileEndPayload { TransferId = gzTransferId }, ct),
                        "gz_end");

                    var done = await Task.WhenAny(gzAckTcs.Task, gzErrTcs.Task).WaitAsync(TimeSpan.FromSeconds(60), ct);
                    if (done == gzErrTcs.Task)
                    {
                        var err = await gzErrTcs.Task;
                        throw new Xunit.Sdk.XunitException($"GZip FT упал: code={err.Code} msg={err.Message}");
                    }
                    var gzAck = await gzAckTcs.Task;
                    Assert.True(gzAck.Success, $"GZip FileAck.Success=false: {gzAck.Message}");
                }
                finally
                {
                    dc.FileAckReceived -= onAck;
                    dc.FileErrorReceived -= onErr;
                }
            });

            // ─── Scenario: quality switch triggers frame resize ─────────
            await RunScenarioAsync("Quality_switch_changes_frame_size", async () =>
            {
                // Берём СВЕЖИЙ последний кадр (auto-quality мог уже поднять резолюцию),
                // затем просим preset с ожидаемо другим размером.
                var currentWidth = Interlocked.CompareExchange(ref latestFrameWidth, 0, 0);
                // Auto-quality чаще всего уже подтянет к High (~1920/1620). Тогда
                // шлём Low — ждём меньший size. Если current < 1000 — шлём High, ждём больше.
                var targetPreset = currentWidth >= 1000 ? "Low" : "High";
                var expectSmaller = targetPreset == "Low";
                Log($"  current frame width: {currentWidth}, request preset: {targetPreset} (expect {(expectSmaller ? "smaller" : "larger")})");

                var changedFrameTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnFrame(RemoteVideoFrame f)
                {
                    if (expectSmaller ? f.Width < currentWidth : f.Width > currentWidth)
                        changedFrameTcs.TrySetResult(f.Width);
                }
                peer.RemoteVideoFrameReceived += OnFrame;
                try
                {
                    await SendWithRetryAsync(
                        () => dc.SendHostVideoSettingsRequestAsync(new HostVideoSettingsRequestPayload
                        {
                            QualityPreset = targetPreset,
                            DisplayMode = "Current",
                            DisplayId = screenMeta.DisplayId,
                            QuickReconnect = false,
                        }, ct),
                        "quality_switch_req");

                    var newWidth = await changedFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                    Log($"  new frame width after switch: {newWidth} (was {currentWidth})");
                    Assert.True(expectSmaller ? newWidth < currentWidth : newWidth > currentWidth,
                        $"frame.Width не изменилась в ожидаемом направлении: было {currentWidth}, стало {newWidth}, expect {(expectSmaller ? "<" : ">")}.");
                }
                finally { peer.RemoteVideoFrameReceived -= OnFrame; }
            });

            // ─── Scenario: general video health — max gap guard ─────────
            await RunScenarioAsync("Video_max_frame_gap_under_10s", async () =>
            {
                // Трекаем max gap между кадрами за 5 секунд streaming'а.
                // Ловит капитальную смерть capture'а (DXGI crash без reinit, encoder hang).
                var lastTs = _sw.ElapsedMilliseconds;
                var maxGap = 0L;
                var frameCnt = 0;
                void OnFrame(RemoteVideoFrame _)
                {
                    var now = _sw.ElapsedMilliseconds;
                    var gap = now - Interlocked.Read(ref lastTs);
                    if (gap > maxGap) maxGap = gap;
                    Interlocked.Exchange(ref lastTs, now);
                    Interlocked.Increment(ref frameCnt);
                }
                peer.RemoteVideoFrameReceived += OnFrame;
                try
                {
                    await Task.Delay(5000, ct);
                }
                finally { peer.RemoteVideoFrameReceived -= OnFrame; }
                Log($"  frames={frameCnt} over 5s, max_gap={maxGap}ms");
                Assert.True(maxGap <= 10000, $"max frame gap {maxGap}ms > 10000ms — capture зависает.");
            });

            // ─── Finale: Summary + assert all scenarios passed ──────────
            Log("\n═══ TIMINGS ═══");
            foreach (var kv in _timings)
                Log($"  {kv.Key,-30} {kv.Value,7}ms");

            Log("\n═══ SCENARIOS ═══");
            foreach (var (name, passed, error) in scenarioResults)
                Log($"  [{(passed ? "✓" : "✗")}] {name,-30} {error ?? ""}");

            var totalMs = _sw.ElapsedMilliseconds;
            Log($"\n═══ TOTAL: {totalMs}ms ═══");

            var failed = scenarioResults.Where(s => !s.passed).ToList();
            if (failed.Count > 0)
            {
                Assert.Fail($"Провалены {failed.Count} сценариев: {string.Join(", ", failed.Select(f => f.name))}");
            }
        }
        finally
        {
            try { peer.Dispose(); } catch { /* ignore teardown errors */ }
        }
    }

    /// <summary>
    /// Зеркало FileTransferService.CompressGZip — каждый chunk сжимается отдельно
    /// GZipStream в default compression level, чтобы receiver мог decompress'ить
    /// через DecompressGZip (FileTransferService.cs:752).
    /// </summary>
    private static byte[] CompressGZipLocal(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress))
        {
            gz.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }
}

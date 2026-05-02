using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WebRtcTransport;

public sealed class WebSocketSignalingClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event Action<SignalingMessage>? MessageReceived;
    public event Action? Disconnected;
    private CancellationTokenSource? _recvCts;
    private Task? _recvTask;

    /// <summary>RTT of the WS TCP handshake in milliseconds. -1 if not measured.</summary>
    public long HandshakeRttMs { get; private set; } = -1;

    /// <summary>Audit fix #3 2026-04-25: server signals auth failure with custom WS close
    /// code 4001 (instead of silent close). Client capture'ит чтобы distinguish "expired
    /// token, refresh + retry" от "network/server failure".
    /// True если последний disconnect был из-за expired/invalid auth token.</summary>
    public bool LastDisconnectWasAuthFailure { get; private set; }
    private const int WsCloseCodeAuthFailed = 4001;

    public WebSocketSignalingClient()
    {
        // Keep WS alive while "host" is waiting for a viewer.
        // This sends WebSocket pings automatically (control frames), preventing idle timeouts.
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public async Task ConnectAsync(string wsUrl, string sessionId, string token, CancellationToken ct = default)
    {
        var uri = BuildUri(wsUrl, sessionId);
        var sw = Stopwatch.StartNew();
        await _socket.ConnectAsync(uri, ct);
        sw.Stop();
        HandshakeRttMs = sw.ElapsedMilliseconds;

        // NET-06: send token as first message instead of query string.
        await SendAsync("auth", sessionId, new { token }, ct);

        _recvCts?.Cancel();
        _recvCts?.Dispose();
        _recvCts = new CancellationTokenSource();
        _recvTask = Task.Run(() => ReceiveLoopAsync(_recvCts.Token));
    }

    public async Task SendAsync(string type, string sessionId, object payload, CancellationToken ct = default)
    {
        if (_socket.State != WebSocketState.Open)
            return;

        var envelope = new
        {
            type,
            session_id = sessionId,
            payload
        };
        var raw = JsonSerializer.Serialize(envelope, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(raw);

        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (WebSocketException)
        {
            // WebSocket was aborted (e.g. VPN dropped) — suppress, Disconnected event will fire from receive loop
        }
        catch (ObjectDisposedException)
        {
            // Already disposed during shutdown
        }
    }

    // Audit fix 2026-04-25 (CRIT-1 / M-4):
    //   1. MaxMessageSize cap = 64 KB (matches server's max-msg cap в signaling/handler.go).
    //      Без лимита malicious peer / compromised server мог бы прислать гигабайты →
    //      MemoryStream аккумулирует → OOM/DoS клиента. Сервер уже cap'ит свой recv,
    //      но client recv = independent boundary (relay через server трактуется как
    //      "untrusted peer payload" поскольку server слепо forwards между peers).
    //   2. Только Text frames принимаем. Binary frames → close connection с reason.
    //      WebRTC signaling это JSON over WS = всегда Text. Binary означает либо
    //      buggy peer либо attacker probing.
    //   3. Per-message read timeout 30 сек — защита от slow-loris fragmentation
    //      (peer шлёт по 1 byte бесконечно).
    private const int MaxMessageBytes = 64 * 1024;
    // Per-FRAGMENT read timeout — применяется ТОЛЬКО для subsequent fragments
    // после первого byte уже прочитан (slow-loris protection: attacker начал
    // отправлять message но не завершает frame).
    //
    // НЕ применяется к waiting for next message (idle WS) — там WebSocket
    // protocol level keepalive (KeepAliveInterval=20s + server pings 15s)
    // отвечает за liveness detection. Если connection truly dead — TCP-level
    // close или server's readDeadline (60s) детектируют.
    //
    // History (debug saga 2026-04-27):
    //  - 30s timeout (commit 754b7ae) — drop'ал idle connections каждые 30 sec
    //    потому что control frames (Ping/Pong) обрабатываются внутри .NET
    //    ClientWebSocket и не возвращаются как WebSocketReceiveResult →
    //    ReceiveAsync блокировался → CancelAfter firing.
    //  - 300s (commit 228c0c9) — improved до 5 min но same issue, just later.
    //  - NOW: применяется только AFTER first fragment received. Idle wait =
    //    no timeout (полагаемся на TCP/server deadline). Slow-loris защита
    //    остаётся: incomplete fragmented frame → 30s timeout → close.
    private const int FragmentTimeoutSeconds = 30;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var segment = new ArraySegment<byte>(buffer);

        try
        {
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                bool oversized = false;
                bool wrongType = false;

                // Two-phase cancellation:
                // - msgCts: linked to ct only (lifetime). Used для FIRST ReceiveAsync —
                //   waiting for next message. NO CancelAfter — idle wait can be hours.
                //   Liveness detection через WS protocol-level keepalive.
                // - После первого fragment'а — apply CancelAfter(FragmentTimeoutSeconds)
                //   для slow-loris protection (attacker started message but never
                //   finishes frame).
                using var msgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bool firstFragment = true;

                do
                {
                    result = await _socket.ReceiveAsync(segment, msgCts.Token);
                    if (firstFragment && !result.EndOfMessage)
                    {
                        // Multi-fragment message started — arm slow-loris timeout
                        // для остальных fragments. EndOfMessage=true means whole
                        // message in one fragment (common case) — timer не нужен.
                        msgCts.CancelAfter(TimeSpan.FromSeconds(FragmentTimeoutSeconds));
                    }
                    firstFragment = false;
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Capture close status — useful для distinguishing auth failure
                        // (server sends 4001) от обычного disconnect.
                        if ((int?)result.CloseStatus == WsCloseCodeAuthFailed)
                        {
                            LastDisconnectWasAuthFailure = true;
                        }
                        Disconnected?.Invoke();
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        // Binary frames not expected — JSON signaling is always Text.
                        // Drain remaining fragments but mark for close после loop'а.
                        wrongType = true;
                    }

                    if (ms.Length + result.Count > MaxMessageBytes)
                    {
                        // Cap reached — продолжаем drain'ить input до EndOfMessage чтобы не
                        // разорвать на середине frame'а, но потом close с PolicyViolation.
                        oversized = true;
                    }
                    else if (!oversized && !wrongType)
                    {
                        ms.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (wrongType || oversized)
                {
                    var reason = wrongType
                        ? WebSocketCloseStatus.InvalidMessageType
                        : WebSocketCloseStatus.MessageTooBig;
                    var description = wrongType
                        ? "expected Text frame"
                        : $"message exceeds {MaxMessageBytes} bytes";
                    try
                    {
                        await _socket.CloseAsync(reason, description, CancellationToken.None);
                    }
                    catch { /* socket may already be torn */ }
                    Disconnected?.Invoke();
                    return;
                }

                var json = Encoding.UTF8.GetString(ms.ToArray());
                var msg = JsonSerializer.Deserialize<SignalingMessage>(json, _jsonOptions);
                if (msg is not null)
                {
                    MessageReceived?.Invoke(msg);
                }
            }
        }
        catch
        {
            Disconnected?.Invoke();
        }
    }

    internal static Uri BuildUri(string wsUrl, string sessionId)
    {
        var separator = wsUrl.Contains('?') ? "&" : "?";
        return new Uri($"{wsUrl}{separator}session_id={Uri.EscapeDataString(sessionId)}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _recvCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", closeCts.Token);
            }
            catch
            {
                // ignore — timeout or already closed
            }
        }
        if (_recvTask is not null)
        {
            try
            {
                await Task.WhenAny(_recvTask, Task.Delay(2000));
            }
            catch
            {
                // ignore
            }
        }
        try { _socket.Abort(); } catch { }
        _recvCts?.Dispose();
        _socket.Dispose();
    }
}

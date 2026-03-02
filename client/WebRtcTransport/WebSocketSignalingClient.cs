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

    public WebSocketSignalingClient()
    {
        // Keep WS alive while "host" is waiting for a viewer.
        // This sends WebSocket pings automatically (control frames), preventing idle timeouts.
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public async Task ConnectAsync(string wsUrl, string sessionId, string token, CancellationToken ct = default)
    {
        var uri = BuildUri(wsUrl, sessionId, token);
        await _socket.ConnectAsync(uri, ct);
        _recvCts?.Cancel();
        _recvCts?.Dispose();
        _recvCts = new CancellationTokenSource();
        _recvTask = Task.Run(() => ReceiveLoopAsync(_recvCts.Token));
    }

    public async Task SendAsync(string type, string sessionId, object payload, CancellationToken ct = default)
    {
        var envelope = new
        {
            type,
            session_id = sessionId,
            payload
        };
        var raw = JsonSerializer.Serialize(envelope, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(raw);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

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
                do
                {
                    result = await _socket.ReceiveAsync(segment, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Disconnected?.Invoke();
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

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

    private static Uri BuildUri(string wsUrl, string sessionId, string token)
    {
        var separator = wsUrl.Contains('?') ? "&" : "?";
        return new Uri($"{wsUrl}{separator}session_id={Uri.EscapeDataString(sessionId)}&token={Uri.EscapeDataString(token)}");
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
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            }
            catch
            {
                // ignore
            }
        }
        if (_recvTask is not null)
        {
            try
            {
                await _recvTask;
            }
            catch
            {
                // ignore
            }
        }
        _recvCts?.Dispose();
        _socket.Dispose();
    }
}

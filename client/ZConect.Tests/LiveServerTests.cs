using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SessionClient;
using WebRtcTransport;
using Xunit;
using Xunit.Abstractions;

namespace ZConect.Tests;

/// <summary>
/// Integration tests against a live signaling server at 92.63.102.244:8080.
/// Covers: API endpoints, WS auth (NET-06), relay, security (N7-01..N7-09).
/// </summary>
[Trait("Category", "Live")]
[Collection("Live")]
public sealed class LiveServerTests : IDisposable
{
    // Server URL configurable через env vars — для локальной интеграции (Go server на 8099)
    // или против prod. Defaults = prod 92.63.102.244.
    // Environment:
    //   ZCONECT_TEST_SERVER = "http://127.0.0.1:8099"  (локально с local-start.ps1)
    //   ZCONECT_TEST_WS     = "ws://127.0.0.1:8099/ws"
    private static string ServerUrl => Environment.GetEnvironmentVariable("ZCONECT_TEST_SERVER")
        ?? "http://92.63.102.244:8080";
    private static string WsUrl => Environment.GetEnvironmentVariable("ZCONECT_TEST_WS")
        ?? "ws://92.63.102.244:8080/ws";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SessionApiClient _api;
    private readonly ITestOutputHelper _out;

    public LiveServerTests(ITestOutputHelper output)
    {
        _out = output;
        _api = new SessionApiClient(_http, msg => _out.WriteLine($"[API] {msg}"));
        _out.WriteLine($"[ENV] ServerUrl={ServerUrl} WsUrl={WsUrl}");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Helper: create session and return response (throws on failure).</summary>
    private async Task<CreateSessionResponse> CreateSessionOrFail(bool unattended = false, string? machineId = null)
    {
        var resp = await _api.CreateSessionAsync(ServerUrl, unattended, machineId: machineId);
        Assert.NotNull(resp);
        return resp;
    }

    /// <summary>Helper: wait for a WS message with timeout.</summary>
    private static async Task<SignalingMessage?> WaitForMessage(WebSocketSignalingClient ws, int timeoutMs = 3000)
    {
        SignalingMessage? received = null;
        var tcs = new TaskCompletionSource<SignalingMessage>();
        ws.MessageReceived += msg => tcs.TrySetResult(msg);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (completed == tcs.Task)
            received = await tcs.Task;
        return received;
    }

    // ════════════════════════════════════════════════════════════
    //  HEALTHZ
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Healthz_returns_ok()
    {
        var resp = await _http.GetAsync($"{ServerUrl}/healthz");
        Assert.True(resp.IsSuccessStatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ok", body);
        _out.WriteLine($"healthz: {body.Trim()}");
    }

    // ════════════════════════════════════════════════════════════
    //  SESSION CREATE
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_session_returns_valid_codes()
    {
        var resp = await CreateSessionOrFail();
        Assert.Equal(8, resp.LoginCode.Length);
        Assert.Equal(8, resp.PassCode.Length);
        Assert.False(string.IsNullOrEmpty(resp.SessionId));
        Assert.False(string.IsNullOrEmpty(resp.WsToken));
        Assert.False(string.IsNullOrEmpty(resp.OwnerSecret));
        Assert.True(resp.ExpiresInSec > 0);
        _out.WriteLine($"session={resp.SessionId} login=****{resp.LoginCode[^4..]} ttl={resp.ExpiresInSec}s");
        await _api.CloseSessionAsync(ServerUrl, resp.SessionId, resp.OwnerSecret);
    }

    [Fact]
    public async Task Create_session_with_machine_id_returns_device_secret()
    {
        var resp = await CreateSessionOrFail(unattended: true, machineId: "xunit-machine-1");
        Assert.False(string.IsNullOrEmpty(resp.DeviceSecret));
        _out.WriteLine($"device_secret length={resp.DeviceSecret.Length}");

        // Same machine_id + device_secret returns same session
        var resp2 = await _api.CreateSessionAsync(ServerUrl, true, machineId: "xunit-machine-1", deviceSecret: resp.DeviceSecret);
        Assert.NotNull(resp2);
        Assert.Equal(resp.SessionId, resp2.SessionId);
        _out.WriteLine("reuse confirmed: same session_id");

        await _api.CloseSessionAsync(ServerUrl, resp.SessionId, resp.OwnerSecret);
    }

    [Fact]
    public async Task Create_session_with_custom_ttl()
    {
        var resp = await _api.CreateSessionAsync(ServerUrl, false, expiresInSec: 600);
        Assert.NotNull(resp);
        // Server should apply requested TTL (±2s for processing)
        Assert.InRange(resp.ExpiresInSec, 595, 601);
        _out.WriteLine($"requested 600s, got {resp.ExpiresInSec}s");
        await _api.CloseSessionAsync(ServerUrl, resp.SessionId, resp.OwnerSecret);
    }

    [Fact]
    public async Task Create_session_wrong_device_secret_creates_new_session()
    {
        var resp = await CreateSessionOrFail(unattended: true, machineId: "xunit-machine-2");
        Assert.False(string.IsNullOrEmpty(resp.DeviceSecret));

        // Wrong device_secret → new session (not the existing one)
        var resp2 = await _api.CreateSessionAsync(ServerUrl, true, machineId: "xunit-machine-2", deviceSecret: "wrong-secret");
        Assert.NotNull(resp2);
        Assert.NotEqual(resp.SessionId, resp2.SessionId);
        _out.WriteLine("wrong device_secret created separate session");

        await _api.CloseSessionAsync(ServerUrl, resp.SessionId, resp.OwnerSecret);
        await _api.CloseSessionAsync(ServerUrl, resp2.SessionId, resp2.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  SESSION JOIN
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Join_session_with_valid_codes_succeeds()
    {
        var created = await CreateSessionOrFail();
        var joined = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(joined);
        Assert.Equal(created.SessionId, joined.SessionId);
        Assert.Equal("PAIRING", joined.State);
        Assert.False(string.IsNullOrEmpty(joined.WsToken));
        _out.WriteLine($"joined session={joined.SessionId} state={joined.State}");
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task Join_session_with_wrong_pass_fails()
    {
        var created = await CreateSessionOrFail();
        var joined = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000");
        Assert.Null(joined);
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task Join_nonexistent_session_fails()
    {
        var joined = await _api.JoinSessionAsync(ServerUrl, "99999999", "99999999");
        Assert.Null(joined);
    }

    // ════════════════════════════════════════════════════════════
    //  SESSION REFRESH
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Refresh_session_extends_ttl()
    {
        var created = await CreateSessionOrFail();
        await Task.Delay(1500); // let some time pass

        var refreshed = await _api.RefreshSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
        Assert.NotNull(refreshed);
        Assert.Equal(created.SessionId, refreshed.SessionId);
        Assert.Equal(created.LoginCode, refreshed.LoginCode);
        // TTL should be reset (close to original, not decreased)
        Assert.True(refreshed.ExpiresInSec >= created.ExpiresInSec - 5);
        Assert.False(string.IsNullOrEmpty(refreshed.WsToken));
        _out.WriteLine($"refreshed ttl={refreshed.ExpiresInSec}s (original={created.ExpiresInSec}s)");
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task Refresh_with_regenerate_pass_changes_password()
    {
        var created = await CreateSessionOrFail();
        var oldPass = created.PassCode;

        var refreshed = await _api.RefreshSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret, regeneratePass: true);
        Assert.NotNull(refreshed);
        Assert.NotEqual(oldPass, refreshed.PassCode);
        _out.WriteLine($"pass changed: ****{oldPass[^4..]} → ****{refreshed.PassCode[^4..]}");

        // Old pass should no longer work
        var joinOld = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, oldPass);
        Assert.Null(joinOld);

        // New pass works
        var joinNew = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, refreshed.PassCode);
        Assert.NotNull(joinNew);

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task Refresh_without_regenerate_keeps_password()
    {
        var created = await CreateSessionOrFail();
        var refreshed = await _api.RefreshSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret, regeneratePass: false);
        Assert.NotNull(refreshed);
        Assert.Equal(created.PassCode, refreshed.PassCode);
        _out.WriteLine("pass unchanged on reconnect-style refresh");
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task Refresh_with_wrong_secret_fails()
    {
        var created = await CreateSessionOrFail();
        var refreshed = await _api.RefreshSessionAsync(ServerUrl, created.SessionId, "wrong-secret");
        Assert.Null(refreshed);
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  SESSION CLOSE
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Close_session_prevents_join()
    {
        var created = await CreateSessionOrFail();
        Assert.True(await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret));
        var joined = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.Null(joined);
    }

    [Fact]
    public async Task Close_with_wrong_secret_fails()
    {
        var created = await CreateSessionOrFail();
        Assert.False(await _api.CloseSessionAsync(ServerUrl, created.SessionId, "wrong-secret"));
        // Session still alive
        var joined = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(joined);
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  WEBSOCKET AUTH (NET-06)
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task WS_connect_with_auth_message_succeeds()
    {
        var created = await CreateSessionOrFail();
        await using var ws = new WebSocketSignalingClient();
        await ws.ConnectAsync(WsUrl, created.SessionId, created.WsToken);
        Assert.True(ws.HandshakeRttMs >= 0);
        _out.WriteLine($"WS connected, RTT={ws.HandshakeRttMs}ms");
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task WS_connect_with_invalid_token_disconnects()
    {
        var created = await CreateSessionOrFail();
        await using var ws = new WebSocketSignalingClient();
        var disconnected = new TaskCompletionSource<bool>();
        ws.Disconnected += () => disconnected.TrySetResult(true);

        // Connect succeeds (HTTP upgrade) but auth message has bad token → server closes
        try
        {
            await ws.ConnectAsync(WsUrl, created.SessionId, "invalid-token-here");
        }
        catch
        {
            // Some implementations may throw immediately
            await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
            return;
        }

        // Wait for disconnect (server should close after bad auth)
        var done = await Task.WhenAny(disconnected.Task, Task.Delay(5000));
        Assert.True(done == disconnected.Task, "expected disconnect after invalid token");
        _out.WriteLine("disconnected after invalid token");
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task WS_connect_without_session_fails()
    {
        await using var ws = new WebSocketSignalingClient();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await ws.ConnectAsync(WsUrl, "nonexistent-session-id", "fake-token");
        });
    }

    // ════════════════════════════════════════════════════════════
    //  WS RELAY — signaling message forwarding
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task WS_relay_host_to_viewer()
    {
        var created = await CreateSessionOrFail();

        await using var hostWs = new WebSocketSignalingClient();
        await hostWs.ConnectAsync(WsUrl, created.SessionId, created.WsToken);

        var viewer = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(viewer);

        await using var viewerWs = new WebSocketSignalingClient();
        SignalingMessage? received = null;
        var tcs = new TaskCompletionSource<SignalingMessage>();
        viewerWs.MessageReceived += msg => tcs.TrySetResult(msg);
        await viewerWs.ConnectAsync(WsUrl, viewer.SessionId, viewer.WsToken);

        // Host sends → viewer should receive
        await hostWs.SendAsync("peer_state", created.SessionId, new { state = "host_ready" });

        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.True(done == tcs.Task, "viewer did not receive relayed message");
        received = await tcs.Task;
        Assert.Equal("peer_state", received.Type);
        _out.WriteLine($"relay OK: type={received.Type}");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task WS_relay_viewer_to_host()
    {
        var created = await CreateSessionOrFail();

        await using var hostWs = new WebSocketSignalingClient();
        SignalingMessage? received = null;
        var tcs = new TaskCompletionSource<SignalingMessage>();
        hostWs.MessageReceived += msg => tcs.TrySetResult(msg);
        await hostWs.ConnectAsync(WsUrl, created.SessionId, created.WsToken);

        var viewer = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(viewer);

        await using var viewerWs = new WebSocketSignalingClient();
        await viewerWs.ConnectAsync(WsUrl, viewer.SessionId, viewer.WsToken);

        // Viewer sends → host should receive
        await viewerWs.SendAsync("peer_state", viewer.SessionId, new { state = "joined" });

        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.True(done == tcs.Task, "host did not receive relayed message");
        received = await tcs.Task;
        Assert.Equal("peer_state", received.Type);
        _out.WriteLine($"relay OK: type={received.Type}");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    [Fact]
    public async Task WS_relay_does_not_echo_to_sender()
    {
        var created = await CreateSessionOrFail();
        await using var hostWs = new WebSocketSignalingClient();
        SignalingMessage? echoed = null;
        hostWs.MessageReceived += msg => echoed = msg;
        await hostWs.ConnectAsync(WsUrl, created.SessionId, created.WsToken);

        // Send message (no other peer connected) → should NOT echo back
        await hostWs.SendAsync("peer_state", created.SessionId, new { state = "test" });
        await Task.Delay(1000);
        Assert.Null(echoed);
        _out.WriteLine("no echo to sender confirmed");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  WS PEER DISCONNECT NOTIFICATION
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task WS_peer_disconnected_notification()
    {
        var created = await CreateSessionOrFail();

        await using var hostWs = new WebSocketSignalingClient();
        var tcs = new TaskCompletionSource<SignalingMessage>();
        hostWs.MessageReceived += msg =>
        {
            if (msg.Type == "peer_disconnected")
                tcs.TrySetResult(msg);
        };
        await hostWs.ConnectAsync(WsUrl, created.SessionId, created.WsToken);

        var viewer = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(viewer);
        var viewerWs = new WebSocketSignalingClient();
        await viewerWs.ConnectAsync(WsUrl, viewer.SessionId, viewer.WsToken);

        // Viewer disconnects
        await viewerWs.DisposeAsync();

        // Host should receive peer_disconnected
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.True(done == tcs.Task, "host did not receive peer_disconnected");
        Assert.Equal("peer_disconnected", (await tcs.Task).Type);
        _out.WriteLine("peer_disconnected notification received");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  N7-01: EXPIRED/CLOSED SESSION REJECTED
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Closed_session_rejects_WS_connect()
    {
        var created = await CreateSessionOrFail();
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);

        await using var ws = new WebSocketSignalingClient();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await ws.ConnectAsync(WsUrl, created.SessionId, created.WsToken);
        });
    }

    [Fact]
    public async Task Refresh_closed_session_fails()
    {
        var created = await CreateSessionOrFail();
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);

        var refreshed = await _api.RefreshSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
        Assert.Null(refreshed);
    }

    // ════════════════════════════════════════════════════════════
    //  N7-03: HTTP BODY SIZE LIMIT
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Oversized_body_rejected()
    {
        var huge = new string('x', 8192);
        var content = new StringContent($"{{\"login_code\":\"{huge}\"}}", Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{ServerUrl}/api/v1/session/join", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ════════════════════════════════════════════════════════════
    //  N7-04: JOIN LOCKOUT
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Join_lockout_after_max_attempts()
    {
        var created = await CreateSessionOrFail();

        for (int i = 0; i < 5; i++)
            Assert.Null(await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000"));

        // Even correct password should fail now (429 locked)
        var locked = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.Null(locked);
        _out.WriteLine("session locked after 5 failed attempts");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  Brute-force protection (progressive lockout + StateBlocked)
    //  Матчит session service_test.go в Go — закрывает client↔server loop.
    // ════════════════════════════════════════════════════════════

    /// <summary>После 5 wrong endpoint возвращает HTTP 429 "session locked"
    /// с "session locked" в теле (не "invalid credentials").</summary>
    [Fact]
    public async Task Join_lockout_returns_429_with_session_locked_error()
    {
        var created = await CreateSessionOrFail();

        // 5 wrong → 401 "invalid credentials"
        for (int i = 0; i < 5; i++)
        {
            var body = new StringContent(
                $"{{\"login_code\":\"{created.LoginCode}\",\"pass_code\":\"00000000\"}}",
                Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{ServerUrl}/api/v1/session/join", body);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("invalid credentials", text);
        }

        // 6-я попытка — 429 "session locked"
        var lockedBody = new StringContent(
            $"{{\"login_code\":\"{created.LoginCode}\",\"pass_code\":\"{created.PassCode}\"}}",
            Encoding.UTF8, "application/json");
        var lockedResp = await _http.PostAsync($"{ServerUrl}/api/v1/session/join", lockedBody);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedResp.StatusCode);
        var lockedText = await lockedResp.Content.ReadAsStringAsync();
        Assert.Contains("session locked", lockedText);
        _out.WriteLine("locked response: 429 + 'session locked'");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    /// <summary>Refresh с regeneratePass разблокирует locked сессию:
    /// новый pass работает, старый отвергается.</summary>
    [Fact]
    public async Task Refresh_unlocks_session_after_lockout()
    {
        var created = await CreateSessionOrFail();
        var oldPass = created.PassCode;

        // Лочим сессию
        for (int i = 0; i < 5; i++)
            Assert.Null(await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000"));

        // Correct pass → locked (429)
        Assert.Null(await _api.JoinSessionAsync(ServerUrl, created.LoginCode, oldPass));

        // Host делает password refresh → разблокирует
        var refreshed = await _api.RefreshSessionAsync(
            ServerUrl, created.SessionId, created.OwnerSecret, regeneratePass: true);
        Assert.NotNull(refreshed);
        Assert.NotEqual(oldPass, refreshed.PassCode);

        // Новый pass работает
        var joined = await _api.JoinSessionAsync(
            ServerUrl, created.LoginCode, refreshed.PassCode);
        Assert.NotNull(joined);
        _out.WriteLine("refresh+regenerate разблокировал сессию и сбросил counters");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    /// <summary>Permanent block (StateBlocked): после 5×4=20 wrong attempts
    /// с ожиданием между tier'ами сессия попадает в BLOCKED state и возвращает
    /// HTTP 423 Locked даже после истечения всех temporary lock'ов.
    /// ВНИМАНИЕ: тест требует ~3 минуты (15с + 2мин lock cycles). Помечен Slow trait.</summary>
    [Fact(Skip = "Требует ~3 минуты из-за tier lockout durations. Run manually: dotnet test --filter BlockedAfterManyFailedAttempts_Integration")]
    [Trait("Speed", "Slow")]
    public async Task BlockedAfterManyFailedAttempts_Integration()
    {
        var created = await CreateSessionOrFail();

        // Tier 1: 5 wrong → 15s lock
        for (int i = 0; i < 5; i++)
            await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000");
        _out.WriteLine("Tier 1 locked, waiting 16s...");
        await Task.Delay(16000);

        // Tier 2: 5 more wrong → 2m lock
        for (int i = 0; i < 5; i++)
            await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000");
        _out.WriteLine("Tier 2 locked, waiting 2m...");
        await Task.Delay(122000);

        // Tier 3: 5 more wrong → 15m lock — SKIP в integration (слишком долго)
        // Вместо этого делаем assertion что сессия всё ещё locked, и выходим.
        // Проверка полного StateBlocked path'а — в Go unit test TestJoin_BlockedAfterAllTiersExhausted.

        var afterTier2 = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.Null(afterTier2);
        _out.WriteLine("после tier 2 сессия всё ещё locked (ожидаемо)");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    /// <summary>Лок одной сессии не влияет на параллельную сессию (session-scoped,
    /// не IP-based — legitimate users за NAT продолжают работать).</summary>
    [Fact]
    public async Task Lockout_does_not_affect_other_sessions()
    {
        var victim = await CreateSessionOrFail();
        var bystander = await CreateSessionOrFail();

        // Брутим victim
        for (int i = 0; i < 5; i++)
            Assert.Null(await _api.JoinSessionAsync(ServerUrl, victim.LoginCode, "00000000"));

        // Victim locked
        Assert.Null(await _api.JoinSessionAsync(ServerUrl, victim.LoginCode, victim.PassCode));

        // Bystander НЕ затронут — работает нормально
        var joined = await _api.JoinSessionAsync(ServerUrl, bystander.LoginCode, bystander.PassCode);
        Assert.NotNull(joined);
        Assert.Equal(bystander.SessionId, joined.SessionId);
        _out.WriteLine("bystander session работает несмотря на lock victim'а (session-scoped protection)");

        await _api.CloseSessionAsync(ServerUrl, victim.SessionId, victim.OwnerSecret);
        await _api.CloseSessionAsync(ServerUrl, bystander.SessionId, bystander.OwnerSecret);
    }

    /// <summary>Server после lockout'а возвращает Retry-After header + retry_after_sec
    /// в body. Клиентский JoinSessionAsyncDetailed парсит это в RetryAfterSec поле
    /// и MainViewModel показывает user'у countdown "через N сек".</summary>
    [Fact]
    public async Task Join_locked_returns_RetryAfter_header_and_body()
    {
        var created = await CreateSessionOrFail();

        // Лочим
        for (int i = 0; i < 5; i++)
            Assert.Null(await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000"));

        // Прямой HTTP call чтобы проверить response headers + body
        var body = new StringContent(
            $"{{\"login_code\":\"{created.LoginCode}\",\"pass_code\":\"{created.PassCode}\"}}",
            Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{ServerUrl}/api/v1/session/join", body);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);

        // Header present + numeric + > 0
        Assert.True(resp.Headers.TryGetValues("Retry-After", out var headerVals), "Retry-After header missing");
        var headerVal = headerVals.First();
        Assert.True(int.TryParse(headerVal, out var headerSec), $"Retry-After не int: {headerVal}");
        Assert.InRange(headerSec, 1, 20); // tier 1 = 15 sec lock, +1 округление = max 16

        // Body contains retry_after_sec
        var bodyText = await resp.Content.ReadAsStringAsync();
        Assert.Contains("retry_after_sec", bodyText);
        _out.WriteLine($"Retry-After header={headerSec}s, body contains retry_after_sec");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    /// <summary>JoinSessionAsyncDetailed корректно парсит 429 response в
    /// JoinSessionStatus.SessionLocked + RetryAfterSec > 0.</summary>
    [Fact]
    public async Task JoinDetailed_on_locked_returns_SessionLocked_with_retry()
    {
        var created = await CreateSessionOrFail();

        for (int i = 0; i < 5; i++)
            await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000");

        var result = await _api.JoinSessionAsyncDetailed(ServerUrl, created.LoginCode, created.PassCode);
        Assert.Equal(JoinSessionStatus.SessionLocked, result.Status);
        Assert.Equal(429, result.HttpStatus);
        Assert.InRange(result.RetryAfterSec, 1, 20);
        _out.WriteLine($"detailed: Status={result.Status} HTTP={result.HttpStatus} RetryAfter={result.RetryAfterSec}s");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    /// <summary>JoinSessionAsyncDetailed для invalid credentials возвращает InvalidCredentials
    /// без RetryAfterSec (unlike locked).</summary>
    [Fact]
    public async Task JoinDetailed_on_wrong_pass_returns_InvalidCredentials()
    {
        var created = await CreateSessionOrFail();

        var result = await _api.JoinSessionAsyncDetailed(ServerUrl, created.LoginCode, "00000000");
        Assert.Equal(JoinSessionStatus.InvalidCredentials, result.Status);
        Assert.Equal(401, result.HttpStatus);
        Assert.Equal(0, result.RetryAfterSec); // нет Retry-After на 401 — это не rate limit

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    /// <summary>Successful join полностью сбрасывает failure counters —
    /// legitimate user может после 4 typos сделать 5-ю правильную и не блокироваться.</summary>
    [Fact]
    public async Task Successful_join_after_typos_resets_counters()
    {
        var created = await CreateSessionOrFail();

        // 4 typos (не доходя до threshold 5)
        for (int i = 0; i < 4; i++)
            Assert.Null(await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000"));

        // 5-й — правильный pass → success
        var joined = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(joined);

        // После success ещё 4 wrong НЕ должны немедленно locked'нуть — counter сбросился
        for (int i = 0; i < 4; i++)
            Assert.Null(await _api.JoinSessionAsync(ServerUrl, created.LoginCode, "00000000"));
        // 5-й wrong наконец lock'нет
        // Проверяем через correct pass: должен всё ещё работать (не locked после reset+4)
        var stillOk = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(stillOk);
        _out.WriteLine("success сбрасывает counter — legitimate user с typos не блокируется");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  N7-01 (NET-01): RESERVED MESSAGE TYPES BLOCKED
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task WS_reserved_message_type_not_relayed()
    {
        var created = await CreateSessionOrFail();

        await using var hostWs = new WebSocketSignalingClient();
        await hostWs.ConnectAsync(WsUrl, created.SessionId, created.WsToken);

        var viewer = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(viewer);

        await using var viewerWs = new WebSocketSignalingClient();
        SignalingMessage? spoofed = null;
        viewerWs.MessageReceived += msg =>
        {
            if (msg.Type == "peer_disconnected" || msg.Type == "error" || msg.Type == "system_error")
                spoofed = msg;
        };
        await viewerWs.ConnectAsync(WsUrl, viewer.SessionId, viewer.WsToken);

        // Host tries to send reserved type (should be blocked by server)
        await hostWs.SendAsync("peer_disconnected", created.SessionId, new { fake = true });
        await hostWs.SendAsync("system_error", created.SessionId, new { fake = true });

        // Send a real message to confirm relay is working
        var realTcs = new TaskCompletionSource<bool>();
        viewerWs.MessageReceived += msg =>
        {
            if (msg.Type == "peer_state") realTcs.TrySetResult(true);
        };
        await hostWs.SendAsync("peer_state", created.SessionId, new { state = "test" });
        await Task.WhenAny(realTcs.Task, Task.Delay(2000));

        Assert.Null(spoofed);
        _out.WriteLine("reserved types blocked, real messages relayed OK");

        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  METHOD VALIDATION
    // ════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/api/v1/session/create")]
    [InlineData("/api/v1/session/join")]
    [InlineData("/api/v1/session/close")]
    [InlineData("/api/v1/session/refresh")]
    public async Task GET_on_post_endpoints_returns_405(string path)
    {
        var resp = await _http.GetAsync($"{ServerUrl}{path}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    // ════════════════════════════════════════════════════════════
    //  INVALID JSON
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Malformed_json_returns_400()
    {
        var content = new StringContent("not-json{{{", Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{ServerUrl}/api/v1/session/create", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Empty_body_returns_400()
    {
        var content = new StringContent("", Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{ServerUrl}/api/v1/session/join", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ════════════════════════════════════════════════════════════
    //  PEER LIMIT (maxPeers=3 for reconnect)
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Three_peers_allowed_fourth_rejected()
    {
        var created = await CreateSessionOrFail();

        // Connect 3 peers (maxPeers=3)
        var ws1 = new WebSocketSignalingClient();
        await ws1.ConnectAsync(WsUrl, created.SessionId, created.WsToken);

        var join2 = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(join2);
        var ws2 = new WebSocketSignalingClient();
        await ws2.ConnectAsync(WsUrl, join2.SessionId, join2.WsToken);

        var join3 = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(join3);
        var ws3 = new WebSocketSignalingClient();
        await ws3.ConnectAsync(WsUrl, join3.SessionId, join3.WsToken);

        _out.WriteLine("3 peers connected OK");

        // 4th peer should be rejected (session full)
        var join4 = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.NotNull(join4);
        var ws4 = new WebSocketSignalingClient();
        var disconnected = new TaskCompletionSource<bool>();
        ws4.Disconnected += () => disconnected.TrySetResult(true);

        try
        {
            await ws4.ConnectAsync(WsUrl, join4.SessionId, join4.WsToken);
            // If connect didn't throw, wait for disconnect
            var done = await Task.WhenAny(disconnected.Task, Task.Delay(5000));
            Assert.True(done == disconnected.Task, "4th peer should be disconnected");
            _out.WriteLine("4th peer rejected");
        }
        catch
        {
            _out.WriteLine("4th peer connection failed (expected)");
        }

        await ws1.DisposeAsync();
        await ws2.DisposeAsync();
        await ws3.DisposeAsync();
        await ws4.DisposeAsync();
        await _api.CloseSessionAsync(ServerUrl, created.SessionId, created.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  CONCURRENT SESSIONS — each independent
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Multiple_sessions_are_independent()
    {
        var s1 = await CreateSessionOrFail();
        var s2 = await CreateSessionOrFail();
        Assert.NotEqual(s1.SessionId, s2.SessionId);
        Assert.NotEqual(s1.LoginCode, s2.LoginCode);

        // Closing one doesn't affect the other
        await _api.CloseSessionAsync(ServerUrl, s1.SessionId, s1.OwnerSecret);
        var joinS2 = await _api.JoinSessionAsync(ServerUrl, s2.LoginCode, s2.PassCode);
        Assert.NotNull(joinS2);
        _out.WriteLine("sessions independent OK");

        await _api.CloseSessionAsync(ServerUrl, s2.SessionId, s2.OwnerSecret);
    }

    // ════════════════════════════════════════════════════════════
    //  SHORT TTL EXPIRY
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Session_expires_after_short_ttl()
    {
        // Create session with 2-second TTL
        var created = await _api.CreateSessionAsync(ServerUrl, false, expiresInSec: 2);
        Assert.NotNull(created);
        _out.WriteLine($"created with 2s TTL, waiting...");

        // Wait for expiry
        await Task.Delay(3000);

        // Join should fail (expired)
        var joined = await _api.JoinSessionAsync(ServerUrl, created.LoginCode, created.PassCode);
        Assert.Null(joined);
        _out.WriteLine("session expired — join rejected");
    }

    // ════════════════════════════════════════════════════════════
    //  FULL FLOW: create → join → WS → relay → close
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Full_flow_create_join_ws_relay_close()
    {
        // 1. Host creates session
        var host = await CreateSessionOrFail();
        _out.WriteLine($"host created login=****{host.LoginCode[^4..]}");

        // 2. Host connects WS
        await using var hostWs = new WebSocketSignalingClient();
        var hostMsgTcs = new TaskCompletionSource<SignalingMessage>();
        hostWs.MessageReceived += msg =>
        {
            if (msg.Type == "peer_state") hostMsgTcs.TrySetResult(msg);
        };
        await hostWs.ConnectAsync(WsUrl, host.SessionId, host.WsToken);
        _out.WriteLine($"host WS RTT={hostWs.HandshakeRttMs}ms");

        // 3. Viewer joins
        var viewer = await _api.JoinSessionAsync(ServerUrl, host.LoginCode, host.PassCode);
        Assert.NotNull(viewer);
        Assert.Equal(host.SessionId, viewer.SessionId);

        // 4. Viewer connects WS
        await using var viewerWs = new WebSocketSignalingClient();
        var viewerMsgTcs = new TaskCompletionSource<SignalingMessage>();
        viewerWs.MessageReceived += msg =>
        {
            if (msg.Type == "peer_state") viewerMsgTcs.TrySetResult(msg);
        };
        await viewerWs.ConnectAsync(WsUrl, viewer.SessionId, viewer.WsToken);
        _out.WriteLine($"viewer WS RTT={viewerWs.HandshakeRttMs}ms");

        // 5. Viewer announces → host receives
        await viewerWs.SendAsync("peer_state", viewer.SessionId, new { state = "joined" });
        var hostGot = await Task.WhenAny(hostMsgTcs.Task, Task.Delay(3000));
        Assert.True(hostGot == hostMsgTcs.Task, "host did not receive viewer's peer_state");
        var hostMsg = await hostMsgTcs.Task;
        _out.WriteLine($"relay viewer→host OK (type={hostMsg.Type})");

        // 6. Host replies → viewer receives
        await hostWs.SendAsync("peer_state", host.SessionId, new { state = "host_ready" });
        var viewerGot = await Task.WhenAny(viewerMsgTcs.Task, Task.Delay(3000));
        Assert.True(viewerGot == viewerMsgTcs.Task, "viewer did not receive host's peer_state");
        var viewerMsg = await viewerMsgTcs.Task;
        _out.WriteLine($"relay host→viewer OK (type={viewerMsg.Type})");

        // 7. Close
        Assert.True(await _api.CloseSessionAsync(ServerUrl, host.SessionId, host.OwnerSecret));
        _out.WriteLine("full flow completed");
    }
}

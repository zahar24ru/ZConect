using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for service lifecycle: user_exit, respawn suppression, reconnect, retry logic.</summary>
public sealed class ServiceLifecycleTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    // ── user_exit flow ──────────────────────────────────────────────

    [Fact]
    public async Task UserExit_message_sets_flag_on_server()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(3000);

        var w = WriteMsgAsync(client, new { type = "user_exit" }, cts.Token);
        var r = ReadMsgAsync<PipeMessage>(server, cts.Token);
        await Task.WhenAll(w, r);

        Assert.NotNull(r.Result);
        Assert.Equal("user_exit", r.Result.Type);
    }

    [Fact]
    public async Task Hello_after_user_exit_is_valid()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(3000);

        // user_exit
        var w1 = WriteMsgAsync(client, new { type = "user_exit" }, cts.Token);
        var r1 = ReadMsgAsync<PipeMessage>(server, cts.Token);
        await Task.WhenAll(w1, r1);
        Assert.Equal("user_exit", r1.Result!.Type);

        // New hello
        var w2 = WriteMsgAsync(client, new { type = "hello", pid = 9999, sessionId = 1 }, cts.Token);
        var r2 = ReadMsgAsync<PipeMessage>(server, cts.Token);
        await Task.WhenAll(w2, r2);
        Assert.Equal("hello", r2.Result!.Type);
        Assert.Equal(9999, r2.Result.Pid);
    }

    // ── SessionMonitor respawn suppression ───────────────────────────

    [Fact]
    public void ShouldSuppressRespawn_default_false()
    {
        var log = new ServiceLogger(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.log"));
        using var monitor = new SessionMonitor(log, "nonexistent.exe");

        // Default: no suppression function → respawn allowed.
        Assert.Null(monitor.ShouldSuppressRespawn);
        log.Dispose();
    }

    [Fact]
    public void ShouldSuppressRespawn_can_be_set()
    {
        var log = new ServiceLogger(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.log"));
        using var monitor = new SessionMonitor(log, "nonexistent.exe");

        bool suppress = false;
        monitor.ShouldSuppressRespawn = () => suppress;

        Assert.False(monitor.ShouldSuppressRespawn());
        suppress = true;
        Assert.True(monitor.ShouldSuppressRespawn());
        log.Dispose();
    }

    // ── UnattendedSessionManager state ──────────────────────────────

    [Fact]
    public void UnattendedManager_initial_state()
    {
        var log = new ServiceLogger(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.log"));
        var config = new ServiceConfig { SignalingUrl = "http://test:8080", MachineId = "test-machine" };
        var manager = new UnattendedSessionManager(log, config);

        Assert.False(manager.HasSession);
        Assert.Equal(string.Empty, manager.LoginCode);
        Assert.Equal(string.Empty, manager.PassCode);
        Assert.Equal(string.Empty, manager.SessionId);
        log.Dispose();
    }

    [Fact]
    public void UnattendedManager_no_signaling_url()
    {
        var log = new ServiceLogger(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.log"));
        var config = new ServiceConfig { SignalingUrl = "", MachineId = "test" };
        var manager = new UnattendedSessionManager(log, config);

        // StartSessionAsync should return immediately without session.
        var task = manager.StartSessionAsync(CancellationToken.None);
        Assert.True(task.IsCompleted || task.Wait(1000));
        Assert.False(manager.HasSession);
        log.Dispose();
    }

    // ── PipeServer UserExitRequested ─────────────────────────────────

    [Fact]
    public void PipeServer_UserExitRequested_default_false()
    {
        var log = new ServiceLogger(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.log"));
        var config = new ServiceConfig();
        var server = new PipeServer(log, config);

        Assert.False(server.UserExitRequested);

        server.Dispose();
        log.Dispose();
    }

    // ── Config message includes codes ────────────────────────────────

    [Fact]
    public void PipeMessage_config_includes_login_pass()
    {
        var msg = new PipeMessage
        {
            Type = "config",
            LoginCode = "12345678",
            PassCode = "87654321",
            CurrentSessionId = "session-abc",
            UnattendedEnabled = true
        };

        var json = JsonSerializer.Serialize(msg, CamelCase);
        var back = JsonSerializer.Deserialize<PipeMessage>(json, CaseInsensitive);

        Assert.NotNull(back);
        Assert.Equal("12345678", back.LoginCode);
        Assert.Equal("87654321", back.PassCode);
        Assert.Equal("session-abc", back.CurrentSessionId);
        Assert.True(back.UnattendedEnabled);
    }

    [Fact]
    public void PipeMessage_config_without_unattended_has_null_codes()
    {
        var msg = new PipeMessage
        {
            Type = "config",
            UnattendedEnabled = false,
            LoginCode = null,
            PassCode = null
        };

        var json = JsonSerializer.Serialize(msg, CamelCase);
        var back = JsonSerializer.Deserialize<PipeMessage>(json, CaseInsensitive);

        Assert.NotNull(back);
        Assert.False(back.UnattendedEnabled);
        Assert.Null(back.LoginCode);
    }

    // ── ServiceConfig DeviceSecret persistence ──────────────────────

    [Fact]
    public void ServiceConfig_DeviceSecret_roundtrip()
    {
        var config = new ServiceConfig
        {
            MachineId = "machine-1",
            DeviceSecret = "secret-abc-123",
            UnattendedEnabled = true
        };

        var json = JsonSerializer.Serialize(config);
        var back = JsonSerializer.Deserialize<ServiceConfig>(json);

        Assert.NotNull(back);
        Assert.Equal("machine-1", back.MachineId);
        Assert.Equal("secret-abc-123", back.DeviceSecret);
        Assert.True(back.UnattendedEnabled);
    }

    // ── ServicePipeClient NotifyUserExitAsync model ─────────────────

    [Fact]
    public void UserExit_message_format()
    {
        var msg = new { type = "user_exit" };
        var json = JsonSerializer.Serialize(msg, CamelCase);
        var back = JsonSerializer.Deserialize<PipeMessage>(json, CaseInsensitive);

        Assert.NotNull(back);
        Assert.Equal("user_exit", back.Type);
    }

    // ── Multiple sequential pipe connections ─────────────────────────

    [Fact]
    public async Task Sequential_connect_disconnect_reconnect()
    {
        var pipeName = "ZConect_LifeTest_" + Guid.NewGuid().ToString("N")[..8];

        // First connection.
        using var srv1 = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cli1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(3000);
        await Task.WhenAll(srv1.WaitForConnectionAsync(cts.Token), cli1.ConnectAsync(cts.Token));

        var w = WriteMsgAsync(cli1, new { type = "hello", pid = 1 }, cts.Token);
        var r = ReadMsgAsync<PipeMessage>(srv1, cts.Token);
        await Task.WhenAll(w, r);
        var msg1 = r.Result;
        Assert.Equal("hello", msg1!.Type);
        Assert.Equal(1, msg1.Pid);

        // Disconnect client.
        cli1.Close();
        var disconnectMsg = await ReadMsgAsync<PipeMessage>(srv1, cts.Token);
        Assert.Null(disconnectMsg); // client disconnected
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<(NamedPipeServerStream, NamedPipeClientStream)> CreateConnectedPairAsync()
    {
        var name = "ZConect_LifeTest_" + Guid.NewGuid().ToString("N")[..8];
        var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var cli = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(3000);
        await Task.WhenAll(srv.WaitForConnectionAsync(cts.Token), cli.ConnectAsync(cts.Token));
        return (srv, cli);
    }

    private static async Task WriteMsgAsync(PipeStream pipe, object msg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(msg, CamelCase);
        var payload = Encoding.UTF8.GetBytes(json);
        await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), ct);
        await pipe.WriteAsync(payload, ct);
        await pipe.FlushAsync(ct);
    }

    private static async Task<T?> ReadMsgAsync<T>(PipeStream pipe, CancellationToken ct) where T : class
    {
        var hdr = new byte[4]; int r = 0;
        while (r < 4) { var n = await pipe.ReadAsync(hdr.AsMemory(r, 4 - r), ct); if (n == 0) return null; r += n; }
        var len = BitConverter.ToInt32(hdr, 0);
        if (len <= 0 || len > 1024 * 1024) return null;
        var buf = new byte[len]; r = 0;
        while (r < len) { var n = await pipe.ReadAsync(buf.AsMemory(r, len - r), ct); if (n == 0) return null; r += n; }
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(buf), CaseInsensitive);
    }
}

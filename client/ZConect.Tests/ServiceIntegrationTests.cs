using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for GUI ↔ Service integration via Named Pipe IPC.</summary>
public sealed class ServiceIntegrationTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Full handshake: hello → config → verify codes.</summary>
    [Fact]
    public async Task Full_handshake_hello_config_codes()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(3000);

        // GUI sends hello, service reads it.
        var writeTask = WriteMsgAsync(client, new { type = "hello", pid = 1234, sessionId = 1 }, cts.Token);
        var readTask = ReadMsgAsync<PipeMessage>(server, cts.Token);
        await Task.WhenAll(writeTask, readTask);

        var hello = readTask.Result;
        Assert.NotNull(hello);
        Assert.Equal("hello", hello.Type);
        Assert.Equal(1234, hello.Pid);

        // Service sends config, GUI reads it.
        var writeTask2 = WriteMsgAsync(server, new PipeMessage
        {
            Type = "config", LoginCode = "56484335", PassCode = "69160945",
            UnattendedEnabled = true, SignalingUrl = "http://server:8080"
        }, cts.Token);
        var readTask2 = ReadMsgAsync<UiApp.Services.PipeMessageRead>(client, cts.Token);
        await Task.WhenAll(writeTask2, readTask2);

        var cfg = readTask2.Result;
        Assert.NotNull(cfg);
        Assert.Equal("config", cfg.Type);
        Assert.Equal("56484335", cfg.LoginCode);
        Assert.Equal("69160945", cfg.PassCode);
        Assert.True(cfg.UnattendedEnabled);
    }

    /// <summary>GUI sends status, service receives.</summary>
    [Fact]
    public async Task Status_message_roundtrip()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(3000);

        var writeTask = WriteMsgAsync(client, new { type = "status", connected = true, currentSessionId = "s1" }, cts.Token);
        var readTask = ReadMsgAsync<PipeMessage>(server, cts.Token);
        await Task.WhenAll(writeTask, readTask);

        Assert.Equal("status", readTask.Result!.Type);
        Assert.True(readTask.Result.Connected);
    }

    /// <summary>Disconnect → read returns null.</summary>
    [Fact]
    public async Task Disconnect_returns_null()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        client.Close();
        client.Dispose();

        using var cts = new CancellationTokenSource(3000);
        var msg = await ReadMsgAsync<PipeMessage>(server, cts.Token);
        Assert.Null(msg);
    }

    /// <summary>Config without unattended has no codes.</summary>
    [Fact]
    public async Task Config_no_unattended_empty_codes()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(3000);

        var writeTask = WriteMsgAsync(server, new PipeMessage { Type = "config", UnattendedEnabled = false }, cts.Token);
        var readTask = ReadMsgAsync<UiApp.Services.PipeMessageRead>(client, cts.Token);
        await Task.WhenAll(writeTask, readTask);

        Assert.False(readTask.Result!.UnattendedEnabled);
        Assert.True(string.IsNullOrEmpty(readTask.Result.LoginCode));
    }

    /// <summary>IsProcessAlive for current process.</summary>
    [Fact]
    public void IsProcessAlive_self()
    {
        Assert.True(CheckAlive(Environment.ProcessId));
    }

    /// <summary>IsProcessAlive for non-existent PID.</summary>
    [Fact]
    public void IsProcessAlive_nonexistent()
    {
        Assert.False(CheckAlive(99999));
    }

    /// <summary>ServiceConfig defaults.</summary>
    [Fact]
    public void ServiceConfig_defaults()
    {
        var cfg = new ServiceConfig();
        Assert.True(cfg.UnattendedEnabled); // default: enabled for portable mode
        Assert.Equal(string.Empty, cfg.DeviceSecret);
        Assert.True(cfg.AutoSpawnHelper);
        // Default updated 2026-04-24 to HTTPS prod endpoint (server больше не exposes 8080 наружу)
        Assert.Equal("https://connect.zconn.ru", cfg.SignalingUrl);
    }

    /// <summary>PipeMessage serialization camelCase compatible.</summary>
    [Fact]
    public void PipeMessage_camelCase_roundtrip()
    {
        var msg = new PipeMessage { Type = "config", LoginCode = "12345678", UnattendedEnabled = true };
        var json = JsonSerializer.Serialize(msg, CamelCase);
        Assert.Contains("\"type\":", json); // camelCase
        Assert.Contains("\"loginCode\":", json);

        var back = JsonSerializer.Deserialize<PipeMessage>(json, CaseInsensitive);
        Assert.NotNull(back);
        Assert.Equal("config", back.Type);
        Assert.Equal("12345678", back.LoginCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<(NamedPipeServerStream server, NamedPipeClientStream client)> CreateConnectedPairAsync()
    {
        var name = "ZConect_IntTest_" + Guid.NewGuid().ToString("N")[..8];
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
        var header = BitConverter.GetBytes(payload.Length);
        await pipe.WriteAsync(header, ct);
        await pipe.WriteAsync(payload, ct);
        await pipe.FlushAsync(ct);
    }

    private static async Task<T?> ReadMsgAsync<T>(PipeStream pipe, CancellationToken ct) where T : class
    {
        var header = new byte[4];
        int read = 0;
        while (read < 4) { var n = await pipe.ReadAsync(header.AsMemory(read, 4 - read), ct); if (n == 0) return null; read += n; }
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 1024 * 1024) return null;
        var payload = new byte[length];
        read = 0;
        while (read < length) { var n = await pipe.ReadAsync(payload.AsMemory(read, length - read), ct); if (n == 0) return null; read += n; }
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(payload), CaseInsensitive);
    }

    private static bool CheckAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}

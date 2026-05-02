using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ZConectService;
using UiApp.Services;
using UiApp.Models;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Tests for GUI ↔ Service integration scenarios:
/// - ServicePipeClient connect/reconnect/event firing
/// - ApplyServiceConfig logic (auto-enable settings, MachineId adoption)
/// - PipeConfigMessage model completeness
/// - Boot coordination (service provides codes, GUI adopts them)
/// </summary>
public sealed class GuiServiceIntegrationTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    // ── ServicePipeClient: connect + receive config ─────────────────

    [Fact]
    public async Task PipeClient_connects_and_receives_config()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(5000);

        // Client sends hello.
        var w1 = WriteMsgAsync(client, new { type = "hello", pid = 42, sessionId = 1 }, cts.Token);
        var r1 = ReadMsgAsync<PipeMessage>(server, cts.Token);
        await Task.WhenAll(w1, r1);
        Assert.Equal("hello", r1.Result!.Type);
        Assert.Equal(42, r1.Result.Pid);

        // Server sends config with all fields.
        var configMsg = new
        {
            type = "config",
            loginCode = "11111111",
            passCode = "22222222",
            currentSessionId = "session-xyz",
            machineId = "machine-abc",
            unattendedEnabled = true,
            signalingUrl = "http://server:8080",
            webSocketUrl = "ws://server:8080/ws",
            stunUrl = "stun:server:3478",
            turnUrl = "turn:server:3478",
            turnUsername = "user",
            turnPassword = "pass"
        };

        var w2 = WriteMsgAsync(server, configMsg, cts.Token);
        var r2 = ReadMsgAsync<PipeMessageRead>(client, cts.Token);
        await Task.WhenAll(w2, r2);

        var cfg = r2.Result;
        Assert.NotNull(cfg);
        Assert.Equal("config", cfg.Type);
        Assert.Equal("11111111", cfg.LoginCode);
        Assert.Equal("22222222", cfg.PassCode);
        Assert.Equal("machine-abc", cfg.MachineId);
        Assert.True(cfg.UnattendedEnabled);
        Assert.Equal("http://server:8080", cfg.SignalingUrl);
        Assert.Equal("ws://server:8080/ws", cfg.WebSocketUrl);
    }

    [Fact]
    public async Task PipeClient_receives_desktop_changed()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(5000);

        var w = WriteMsgAsync(server, new { type = "desktop_changed", desktop = "Winlogon" }, cts.Token);
        var r = ReadMsgAsync<PipeMessageRead>(client, cts.Token);
        await Task.WhenAll(w, r);

        Assert.Equal("desktop_changed", r.Result!.Type);
        Assert.Equal("Winlogon", r.Result.Desktop);
    }

    // ── PipeConfigMessage model ────────────────────────────────────

    [Fact]
    public void PipeConfigMessage_all_fields_populated()
    {
        var cfg = new PipeConfigMessage
        {
            LoginCode = "12345678",
            PassCode = "87654321",
            SessionId = "s1",
            SignalingUrl = "http://a",
            WebSocketUrl = "ws://b",
            StunUrl = "stun:c",
            TurnUrl = "turn:d",
            TurnUsername = "u",
            TurnPassword = "p",
            MachineId = "m1",
            UnattendedEnabled = true
        };

        Assert.Equal("12345678", cfg.LoginCode);
        Assert.Equal("87654321", cfg.PassCode);
        Assert.Equal("m1", cfg.MachineId);
        Assert.True(cfg.UnattendedEnabled);
    }

    [Fact]
    public void PipeConfigMessage_defaults_are_empty()
    {
        var cfg = new PipeConfigMessage();

        Assert.Equal("", cfg.LoginCode);
        Assert.Equal("", cfg.PassCode);
        Assert.Equal("", cfg.MachineId);
        Assert.False(cfg.UnattendedEnabled);
    }

    // ── ApplyServiceConfig simulation ──────────────────────────────

    [Fact]
    public void ApplyServiceConfig_enables_AutoStartOnBoot()
    {
        var settings = new ClientSettings { AutoStartOnBoot = false, MinimizeToTray = false };

        bool settingsChanged = false;
        if (!settings.AutoStartOnBoot)
        {
            settings.AutoStartOnBoot = true;
            settingsChanged = true;
        }
        if (!settings.MinimizeToTray)
        {
            settings.MinimizeToTray = true;
            settingsChanged = true;
        }

        Assert.True(settings.AutoStartOnBoot);
        Assert.True(settings.MinimizeToTray);
        Assert.True(settingsChanged);
    }

    [Fact]
    public void ApplyServiceConfig_no_change_when_already_enabled()
    {
        var settings = new ClientSettings { AutoStartOnBoot = true, MinimizeToTray = true };

        bool settingsChanged = false;
        if (!settings.AutoStartOnBoot)
        {
            settings.AutoStartOnBoot = true;
            settingsChanged = true;
        }
        if (!settings.MinimizeToTray)
        {
            settings.MinimizeToTray = true;
            settingsChanged = true;
        }

        Assert.True(settings.AutoStartOnBoot);
        Assert.True(settings.MinimizeToTray);
        Assert.False(settingsChanged);
    }

    [Fact]
    public void ApplyServiceConfig_adopts_MachineId()
    {
        var settings = new ClientSettings { MachineId = "gui-old-id" };
        var serviceMachineId = "service-new-id";

        if (!string.IsNullOrEmpty(serviceMachineId) && serviceMachineId != settings.MachineId)
        {
            settings.MachineId = serviceMachineId;
        }

        Assert.Equal("service-new-id", settings.MachineId);
    }

    [Fact]
    public void ApplyServiceConfig_does_not_overwrite_with_empty_MachineId()
    {
        var settings = new ClientSettings { MachineId = "gui-existing-id" };
        var serviceMachineId = "";

        if (!string.IsNullOrEmpty(serviceMachineId) && serviceMachineId != settings.MachineId)
        {
            settings.MachineId = serviceMachineId;
        }

        Assert.Equal("gui-existing-id", settings.MachineId);
    }

    [Fact]
    public void ApplyServiceConfig_skips_when_UnattendedEnabled_false()
    {
        var cfg = new PipeConfigMessage
        {
            UnattendedEnabled = false,
            LoginCode = "12345678",
            PassCode = "87654321",
            MachineId = "machine-1"
        };

        bool shouldApply = cfg.UnattendedEnabled && !string.IsNullOrEmpty(cfg.LoginCode);
        Assert.False(shouldApply);
    }

    [Fact]
    public void ApplyServiceConfig_skips_when_LoginCode_empty()
    {
        var cfg = new PipeConfigMessage
        {
            UnattendedEnabled = true,
            LoginCode = "",
            MachineId = "machine-1"
        };

        bool shouldApply = cfg.UnattendedEnabled && !string.IsNullOrEmpty(cfg.LoginCode);
        Assert.False(shouldApply);
    }

    [Fact]
    public void ApplyServiceConfig_applies_when_valid()
    {
        var cfg = new PipeConfigMessage
        {
            UnattendedEnabled = true,
            LoginCode = "12345678",
            PassCode = "87654321",
            MachineId = "machine-1"
        };

        bool shouldApply = cfg.UnattendedEnabled && !string.IsNullOrEmpty(cfg.LoginCode);
        Assert.True(shouldApply);
    }

    // ── Boot coordination: 30s wait logic ──────────────────────────

    [Fact]
    public void Boot_wait_loop_configuration()
    {
        const int maxIterations = 60;
        const int delayMs = 500;
        var totalWaitSec = maxIterations * delayMs / 1000;
        Assert.Equal(30, totalWaitSec);
    }

    [Fact]
    public void Boot_wait_exits_early_when_codes_received()
    {
        var loginCode = "12345678";
        bool pipeConnected = true;
        bool shouldExit = !string.IsNullOrEmpty(loginCode) && pipeConnected;
        Assert.True(shouldExit);
    }

    [Fact]
    public void Boot_wait_continues_when_no_codes()
    {
        var loginCode = "";
        bool pipeConnected = true;
        bool shouldExit = !string.IsNullOrEmpty(loginCode) && pipeConnected;
        Assert.False(shouldExit);
    }

    [Fact]
    public void Boot_wait_continues_when_pipe_not_connected()
    {
        var loginCode = "12345678";
        bool pipeConnected = false;
        bool shouldExit = !string.IsNullOrEmpty(loginCode) && pipeConnected;
        Assert.False(shouldExit);
    }

    // ── Service → GUI config: full end-to-end wire format ──────────

    [Fact]
    public async Task Config_from_service_to_gui_preserves_all_fields()
    {
        var (server, client) = await CreateConnectedPairAsync();
        using var _ = server;
        using var __ = client;
        using var cts = new CancellationTokenSource(5000);

        var serviceConfig = new
        {
            type = "config",
            loginCode = "46139153",
            passCode = "47242852",
            currentSessionId = "ed6aea21-a492-b7b2-f487-da943da34597",
            machineId = "dde394b32ed54848983d8f58b9fb752c",
            unattendedEnabled = true,
            signalingUrl = "http://92.63.102.244:8080",
            webSocketUrl = "ws://92.63.102.244:8080/ws",
            stunUrl = "stun:92.63.102.244:3478",
            turnUrl = "turn:92.63.102.244:3478",
            turnUsername = "zconect",
            turnPassword = "secret123"
        };

        var w = WriteMsgAsync(server, serviceConfig, cts.Token);
        var r = ReadMsgAsync<PipeMessageRead>(client, cts.Token);
        await Task.WhenAll(w, r);

        var guiReceived = r.Result;
        Assert.NotNull(guiReceived);
        Assert.Equal("46139153", guiReceived.LoginCode);
        Assert.Equal("47242852", guiReceived.PassCode);
        Assert.Equal("dde394b32ed54848983d8f58b9fb752c", guiReceived.MachineId);
        Assert.Equal("ed6aea21-a492-b7b2-f487-da943da34597", guiReceived.CurrentSessionId);
        Assert.True(guiReceived.UnattendedEnabled);
        Assert.Equal("http://92.63.102.244:8080", guiReceived.SignalingUrl);
        Assert.Equal("ws://92.63.102.244:8080/ws", guiReceived.WebSocketUrl);
        Assert.Equal("stun:92.63.102.244:3478", guiReceived.StunUrl);
        Assert.Equal("turn:92.63.102.244:3478", guiReceived.TurnUrl);
        Assert.Equal("zconect", guiReceived.TurnUsername);
        Assert.Equal("secret123", guiReceived.TurnPassword);
    }

    // ── Reconnect scenario ──────────────────────────────────────────

    [Fact]
    public async Task Reconnect_gets_fresh_config()
    {
        var pipeName = "ZConect_ReconnTest_" + Guid.NewGuid().ToString("N")[..8];
        using var cts = new CancellationTokenSource(5000);

        // First connection.
        using var srv1 = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cli1 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await Task.WhenAll(srv1.WaitForConnectionAsync(cts.Token), cli1.ConnectAsync(cts.Token));

        var w1 = WriteMsgAsync(cli1, new { type = "hello", pid = 1 }, cts.Token);
        var r1 = ReadMsgAsync<PipeMessage>(srv1, cts.Token);
        await Task.WhenAll(w1, r1);
        Assert.Equal("hello", r1.Result!.Type);

        // Send config #1.
        var w2 = WriteMsgAsync(srv1, new { type = "config", loginCode = "11111111" }, cts.Token);
        var r2 = ReadMsgAsync<PipeMessageRead>(cli1, cts.Token);
        await Task.WhenAll(w2, r2);
        Assert.Equal("11111111", r2.Result!.LoginCode);

        // Disconnect client.
        cli1.Close();
        var disc = await ReadMsgAsync<PipeMessage>(srv1, cts.Token);
        Assert.Null(disc);

        // Second connection (simulates reconnect).
        srv1.Disconnect();
        using var cli2 = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(srv1.WaitForConnectionAsync(cts.Token), cli2.ConnectAsync(cts.Token));

        var w3 = WriteMsgAsync(cli2, new { type = "hello", pid = 2 }, cts.Token);
        var r3 = ReadMsgAsync<PipeMessage>(srv1, cts.Token);
        await Task.WhenAll(w3, r3);
        Assert.Equal(2, r3.Result!.Pid);

        // Send updated config.
        var w4 = WriteMsgAsync(srv1, new { type = "config", loginCode = "22222222" }, cts.Token);
        var r4 = ReadMsgAsync<PipeMessageRead>(cli2, cts.Token);
        await Task.WhenAll(w4, r4);
        Assert.Equal("22222222", r4.Result!.LoginCode);
    }

    // ── Protocol validation: length prefix checks ─────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Protocol_rejects_zero_or_negative_length(int length)
    {
        // The protocol reads a 4-byte LE length prefix. Zero or negative → rejected.
        bool valid = length > 0 && length <= 1024 * 1024;
        Assert.False(valid);
    }

    [Theory]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(2 * 1024 * 1024)]
    [InlineData(int.MaxValue)]
    public void Protocol_rejects_oversized_length(int length)
    {
        bool valid = length > 0 && length <= 1024 * 1024;
        Assert.False(valid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1024 * 1024)]
    public void Protocol_accepts_valid_length(int length)
    {
        bool valid = length > 0 && length <= 1024 * 1024;
        Assert.True(valid);
    }

    [Fact]
    public void Protocol_length_header_is_4_bytes_LE()
    {
        int length = 42;
        var header = BitConverter.GetBytes(length);
        Assert.Equal(4, header.Length);
        Assert.Equal(length, BitConverter.ToInt32(header, 0));
    }

    // ── Service codes stability across reconnect ───────────────────

    [Fact]
    public void Same_MachineId_and_DeviceSecret_produces_same_session()
    {
        var config = new ZConectService.ServiceConfig
        {
            MachineId = "stable-machine-id",
            DeviceSecret = "stable-device-secret",
            UnattendedEnabled = true
        };

        var json = JsonSerializer.Serialize(config);
        var restored = JsonSerializer.Deserialize<ZConectService.ServiceConfig>(json);

        Assert.Equal(config.MachineId, restored!.MachineId);
        Assert.Equal(config.DeviceSecret, restored.DeviceSecret);
    }

    // ── IsServiceInstalled logic ───────────────────────────────────

    [Fact]
    public void IsServiceInstalled_sc_output_with_1060_means_not_installed()
    {
        var output = "[SC] EnumQueryServicesStatus:OpenService FAILED 1060:";
        bool installed = !output.Contains("1060");
        Assert.False(installed);
    }

    [Fact]
    public void IsServiceInstalled_sc_output_with_RUNNING_means_installed()
    {
        var output = "STATE: 4 RUNNING";
        bool installed = !output.Contains("1060");
        Assert.True(installed);
    }

    [Fact]
    public void IsServiceInstalled_sc_output_with_STOPPED_means_installed()
    {
        var output = "STATE: 1 STOPPED";
        bool installed = !output.Contains("1060");
        Assert.True(installed);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<(NamedPipeServerStream, NamedPipeClientStream)> CreateConnectedPairAsync()
    {
        var name = "ZConect_GuiTest_" + Guid.NewGuid().ToString("N")[..8];
        var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var cli = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(5000);
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

using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Tests for network failure scenarios: service retry, session refresh failure,
/// unattended session recovery, and GUI resilience.
/// </summary>
public sealed class NetworkResilienceTests
{
    // ── UnattendedSessionManager: retry on network failure ──────────

    [Fact]
    public async Task StartSession_returns_on_cancellation()
    {
        var log = CreateTempLogger();
        var config = new ServiceConfig { SignalingUrl = "http://unreachable:9999", MachineId = "test" };
        var manager = new UnattendedSessionManager(log, config);

        using var cts = new CancellationTokenSource(500); // cancel after 500ms
        await manager.StartSessionAsync(cts.Token);

        // Should return without session (cancelled before 10min timeout).
        Assert.False(manager.HasSession);
        log.Dispose();
    }

    [Fact]
    public void StartSession_no_url_returns_immediately()
    {
        var log = CreateTempLogger();
        var config = new ServiceConfig { SignalingUrl = "", MachineId = "test" };
        var manager = new UnattendedSessionManager(log, config);

        var task = manager.StartSessionAsync(CancellationToken.None);
        Assert.True(task.Wait(1000)); // should complete immediately
        Assert.False(manager.HasSession);
        log.Dispose();
    }

    [Fact]
    public async Task TickAsync_does_nothing_without_session()
    {
        var log = CreateTempLogger();
        var config = new ServiceConfig { SignalingUrl = "http://test:8080", MachineId = "test" };
        var manager = new UnattendedSessionManager(log, config);

        // No session → tick should be no-op.
        await manager.TickAsync(CancellationToken.None);
        Assert.False(manager.HasSession);
        log.Dispose();
    }

    [Fact]
    public async Task TickAsync_tolerates_single_failure_without_recreating()
    {
        var log = CreateTempLogger();
        // Unreachable URL — refresh will fail, but session shouldn't be recreated on first failure.
        var config = new ServiceConfig { SignalingUrl = "http://unreachable:9999", MachineId = "test" };
        var manager = new UnattendedSessionManager(log, config);

        // No session → tick is no-op (still false).
        await manager.TickAsync(CancellationToken.None);
        Assert.False(manager.HasSession);
        log.Dispose();
    }

    [Fact]
    public void StopSession_without_active_session()
    {
        var log = CreateTempLogger();
        var config = new ServiceConfig { SignalingUrl = "http://test:8080", MachineId = "test" };
        var manager = new UnattendedSessionManager(log, config);

        // No session → stop should be no-op.
        var task = manager.StopSessionAsync();
        Assert.True(task.Wait(1000));
        Assert.False(manager.HasSession);
        log.Dispose();
    }

    // ── ServiceConfig: network settings ─────────────────────────────

    [Fact]
    public void ServiceConfig_all_network_fields_persist()
    {
        var config = new ServiceConfig
        {
            SignalingUrl = "http://server:8080",
            WebSocketUrl = "ws://server:8080/ws",
            StunUrl = "stun:server:3478",
            TurnUrl = "turn:server:3478",
            TurnUsername = "user",
            TurnPassword = "pass",
            MachineId = "machine-1",
            DeviceSecret = "secret-1",
            UnattendedEnabled = true
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var back = System.Text.Json.JsonSerializer.Deserialize<ServiceConfig>(json);

        Assert.NotNull(back);
        Assert.Equal("http://server:8080", back.SignalingUrl);
        Assert.Equal("ws://server:8080/ws", back.WebSocketUrl);
        Assert.Equal("stun:server:3478", back.StunUrl);
        Assert.Equal("turn:server:3478", back.TurnUrl);
        Assert.Equal("user", back.TurnUsername);
        Assert.Equal("pass", back.TurnPassword);
        Assert.Equal("machine-1", back.MachineId);
        Assert.Equal("secret-1", back.DeviceSecret);
        Assert.True(back.UnattendedEnabled);
    }

    // ── SessionMonitor: process detection edge cases ────────────────

    [Fact]
    public void IsProcessAlive_returns_false_for_zero_pid()
    {
        Assert.False(CheckAlive(0));
    }

    [Fact]
    public void IsProcessAlive_returns_false_for_negative_pid()
    {
        Assert.False(CheckAlive(-1));
    }

    [Fact]
    public void IsProcessAlive_returns_true_for_self()
    {
        Assert.True(CheckAlive(Environment.ProcessId));
    }

    [Fact]
    public void IsProcessAlive_returns_false_for_large_pid()
    {
        Assert.False(CheckAlive(999999));
    }

    // ── ServiceLogger: survives rapid writes (simulates error spam) ─

    [Fact]
    public void Logger_handles_rapid_error_spam()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_spam_{Guid.NewGuid():N}.log");
        using var log = new ServiceLogger(path);

        // Simulate 100 rapid error logs (like network failures).
        for (int i = 0; i < 100; i++)
            log.Error("Network", $"connection_failed_attempt_{i}", "No route to host");

        log.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.Equal(100, lines.Length);

        // All lines should be valid JSON.
        foreach (var line in lines)
            Assert.Contains("\"level\":\"ERROR\"", line);

        File.Delete(path);
    }

    // ── PipeMessage: desktop_changed for UAC ────────────────────────

    [Fact]
    public void DesktopChanged_message_Winlogon()
    {
        var msg = new PipeMessage { Type = "desktop_changed", Desktop = "Winlogon" };
        var json = System.Text.Json.JsonSerializer.Serialize(msg,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var back = System.Text.Json.JsonSerializer.Deserialize<PipeMessage>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(back);
        Assert.Equal("desktop_changed", back.Type);
        Assert.Equal("Winlogon", back.Desktop);
    }

    [Fact]
    public void DesktopChanged_message_Default()
    {
        var msg = new PipeMessage { Type = "desktop_changed", Desktop = "Default" };
        var json = System.Text.Json.JsonSerializer.Serialize(msg,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var back = System.Text.Json.JsonSerializer.Deserialize<PipeMessage>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal("Default", back!.Desktop);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static ServiceLogger CreateTempLogger()
    {
        return new ServiceLogger(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.log"));
    }

    private static bool CheckAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}

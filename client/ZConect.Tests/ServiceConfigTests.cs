using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for service configuration persistence.</summary>
public sealed class ServiceConfigTests
{
    [Fact]
    public void Default_config_has_empty_fields()
    {
        var config = new ServiceConfig();
        Assert.Equal(string.Empty, config.MachineId);
        // Default updated 2026-04-24 to HTTPS prod endpoint (was http://92.63.102.244:8080)
        Assert.Equal("https://connect.zconn.ru", config.SignalingUrl);
        Assert.Equal(string.Empty, config.UnattendedPassword);
        Assert.True(config.UnattendedEnabled);
        Assert.True(config.AutoSpawnHelper);
    }

    [Fact]
    public void Config_serializes_and_deserializes()
    {
        var config = new ServiceConfig
        {
            MachineId = "test-machine-123",
            SignalingUrl = "http://example.com:8080",
            TurnUsername = "user",
            UnattendedEnabled = true,
            AutoSpawnHelper = false
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<ServiceConfig>(json);

        Assert.NotNull(loaded);
        Assert.Equal("test-machine-123", loaded.MachineId);
        Assert.Equal("http://example.com:8080", loaded.SignalingUrl);
        Assert.Equal("user", loaded.TurnUsername);
        Assert.True(loaded.UnattendedEnabled);
        Assert.False(loaded.AutoSpawnHelper);
    }

    [Fact]
    public void Config_has_all_network_fields()
    {
        var config = new ServiceConfig
        {
            SignalingUrl = "http://server:8080",
            WebSocketUrl = "ws://server:8080/ws",
            StunUrl = "stun:server:3478",
            TurnUrl = "turn:server:3478",
            TurnUsername = "zconect",
            TurnPassword = "secret"
        };

        Assert.Equal("http://server:8080", config.SignalingUrl);
        Assert.Equal("ws://server:8080/ws", config.WebSocketUrl);
        Assert.Equal("stun:server:3478", config.StunUrl);
        Assert.Equal("turn:server:3478", config.TurnUrl);
        Assert.Equal("zconect", config.TurnUsername);
        Assert.Equal("secret", config.TurnPassword);
    }

    [Fact]
    public void HelperExePath_defaults_to_empty()
    {
        var config = new ServiceConfig();
        Assert.Equal(string.Empty, config.HelperExePath);
    }
}

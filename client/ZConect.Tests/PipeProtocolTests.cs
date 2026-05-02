using System.Text;
using System.Text.Json;
using ZConectService;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for Named Pipe IPC protocol (wire format, serialization).</summary>
public sealed class PipeProtocolTests
{
    [Fact]
    public void PipeName_is_consistent()
    {
        Assert.Equal("ZConect_Service_IPC", PipeProtocol.PipeName);
    }

    [Fact]
    public void PipeMessage_has_all_fields()
    {
        var msg = new PipeMessage
        {
            Type = "test",
            Pid = 100,
            SessionId = 3,
            Connected = true,
            CurrentSessionId = "sess-1",
            LoginCode = "12345678",
            PassCode = "87654321",
            SignalingUrl = "http://x",
            WebSocketUrl = "ws://x",
            StunUrl = "stun:x",
            TurnUrl = "turn:x",
            TurnUsername = "u",
            TurnPassword = "p",
            MachineId = "m",
            UnattendedEnabled = true,
            Desktop = "Winlogon"
        };

        Assert.Equal("test", msg.Type);
        Assert.Equal(100, msg.Pid);
        Assert.Equal("Winlogon", msg.Desktop);
        Assert.Equal("12345678", msg.LoginCode);
        Assert.True(msg.UnattendedEnabled);
    }

    [Fact]
    public void WireFormat_is_length_prefixed_json()
    {
        // Simulate the wire format: [4 bytes LE length] [UTF-8 JSON]
        var msg = new PipeMessage { Type = "hello", Pid = 42, SessionId = 1 };
        var json = JsonSerializer.Serialize(msg);
        var payload = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(payload.Length);

        // Wire = header + payload
        var wire = new byte[header.Length + payload.Length];
        header.CopyTo(wire, 0);
        payload.CopyTo(wire, 4);

        // Parse back
        var parsedLength = BitConverter.ToInt32(wire, 0);
        Assert.Equal(payload.Length, parsedLength);

        var parsedJson = Encoding.UTF8.GetString(wire, 4, parsedLength);
        var parsed = JsonSerializer.Deserialize<PipeMessage>(parsedJson);

        Assert.NotNull(parsed);
        Assert.Equal("hello", parsed.Type);
        Assert.Equal(42, parsed.Pid);
        Assert.Equal(1, parsed.SessionId);
    }

    [Fact]
    public void Config_message_serializes_all_fields()
    {
        var config = new PipeMessage
        {
            Type = "config",
            SignalingUrl = "http://server:8080",
            WebSocketUrl = "ws://server:8080/ws",
            StunUrl = "stun:server:3478",
            TurnUrl = "turn:server:3478",
            TurnUsername = "zconect",
            TurnPassword = "secret123",
            MachineId = "machine-abc",
            UnattendedEnabled = true
        };

        var json = JsonSerializer.Serialize(config);
        var back = JsonSerializer.Deserialize<PipeMessage>(json);

        Assert.NotNull(back);
        Assert.Equal("config", back.Type);
        Assert.Equal("http://server:8080", back.SignalingUrl);
        Assert.Equal("ws://server:8080/ws", back.WebSocketUrl);
        Assert.Equal("stun:server:3478", back.StunUrl);
        Assert.Equal("turn:server:3478", back.TurnUrl);
        Assert.Equal("zconect", back.TurnUsername);
        Assert.Equal("secret123", back.TurnPassword);
        Assert.Equal("machine-abc", back.MachineId);
        Assert.True(back.UnattendedEnabled);
    }

    [Fact]
    public void Status_message_serializes()
    {
        var status = new PipeMessage
        {
            Type = "status",
            Connected = true,
            CurrentSessionId = "abc-123",
            LoginCode = "12345678",
            PassCode = "87654321"
        };

        var json = JsonSerializer.Serialize(status);
        var back = JsonSerializer.Deserialize<PipeMessage>(json);

        Assert.NotNull(back);
        Assert.Equal("status", back.Type);
        Assert.True(back.Connected);
        Assert.Equal("abc-123", back.CurrentSessionId);
        Assert.Equal("12345678", back.LoginCode);
    }

    [Fact]
    public void Desktop_changed_message_serializes()
    {
        var msg = new PipeMessage { Type = "desktop_changed", Desktop = "Winlogon" };
        var json = JsonSerializer.Serialize(msg);
        var back = JsonSerializer.Deserialize<PipeMessage>(json);

        Assert.NotNull(back);
        Assert.Equal("desktop_changed", back.Type);
        Assert.Equal("Winlogon", back.Desktop);
    }
}

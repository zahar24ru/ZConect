using WebRtcTransport;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for WebSocketSignalingClient.BuildUri — WS connection URI construction.</summary>
public sealed class WebSocketUriTests
{
    [Fact]
    public void BuildUri_AppendsSessionId_WithoutToken()
    {
        var uri = WebSocketSignalingClient.BuildUri("wss://example.com/ws", "sess123");

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("example.com", uri.Host);
        Assert.Equal("/ws", uri.AbsolutePath);
        Assert.Contains("session_id=sess123", uri.Query);
        // NET-06: token must NOT be in the URL
        Assert.DoesNotContain("token", uri.Query);
        Assert.StartsWith("?", uri.Query);
    }

    [Fact]
    public void BuildUri_UsesAmpersand_WhenQueryAlreadyExists()
    {
        var uri = WebSocketSignalingClient.BuildUri("wss://example.com/ws?foo=bar", "sess1");

        Assert.Contains("foo=bar", uri.Query);
        Assert.Contains("session_id=sess1", uri.Query);
        Assert.DoesNotContain("token", uri.Query);
        Assert.Contains("&session_id=", uri.ToString());
    }

    [Fact]
    public void BuildUri_EscapesSpecialCharacters()
    {
        var uri = WebSocketSignalingClient.BuildUri("wss://example.com/ws", "sess with spaces");

        var originalStr = uri.OriginalString;
        Assert.DoesNotContain(" ", originalStr);
        Assert.Contains("session_id=sess%20with%20spaces", originalStr);
        Assert.DoesNotContain("token", originalStr);
    }

    [Fact]
    public void BuildUri_HandlesWsScheme()
    {
        var uri = WebSocketSignalingClient.BuildUri("ws://localhost:8080/ws", "s1");

        Assert.Equal("ws", uri.Scheme);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(8080, uri.Port);
    }

    [Fact]
    public void BuildUri_HandlesPathOnly()
    {
        var uri = WebSocketSignalingClient.BuildUri("wss://host.com/api/v1/ws", "abc");

        Assert.Equal("/api/v1/ws", uri.AbsolutePath);
        Assert.Contains("session_id=abc", uri.Query);
        Assert.DoesNotContain("token", uri.Query);
    }
}

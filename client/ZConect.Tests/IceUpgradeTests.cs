using UiApp.Services;
using Xunit;

namespace ZConect.Tests;

public class IceUpgradeTests
{
    // ── GetAdaptiveInterval ────────────────────────────────────────────

    [Fact]
    public void GetAdaptiveInterval_Relay_FirstCheck_Returns15s()
    {
        var interval = IceUpgradeOptimizer.GetAdaptiveInterval("relay", consecutiveRelayCount: 0, rttMs: 200);
        Assert.Equal(15_000, interval);
    }

    [Fact]
    public void GetAdaptiveInterval_Relay_SecondCheck_Returns30s()
    {
        var interval = IceUpgradeOptimizer.GetAdaptiveInterval("relay", consecutiveRelayCount: 1, rttMs: 200);
        Assert.Equal(30_000, interval);
    }

    [Fact]
    public void GetAdaptiveInterval_Relay_ThirdCheck_Returns60s()
    {
        var interval = IceUpgradeOptimizer.GetAdaptiveInterval("relay", consecutiveRelayCount: 2, rttMs: 200);
        Assert.Equal(60_000, interval);
    }

    [Fact]
    public void GetAdaptiveInterval_Relay_FourthCheck_Returns120s()
    {
        var interval = IceUpgradeOptimizer.GetAdaptiveInterval("relay", consecutiveRelayCount: 3, rttMs: 200);
        Assert.Equal(120_000, interval);
    }

    [Fact]
    public void GetAdaptiveInterval_Relay_ManyFailures_Returns300s()
    {
        var interval = IceUpgradeOptimizer.GetAdaptiveInterval("relay", consecutiveRelayCount: 10, rttMs: 200);
        Assert.Equal(300_000, interval);
    }

    [Fact]
    public void GetAdaptiveInterval_Host_Returns300s()
    {
        var interval = IceUpgradeOptimizer.GetAdaptiveInterval("host", consecutiveRelayCount: 0, rttMs: 10);
        Assert.Equal(300_000, interval);
    }

    [Fact]
    public void GetAdaptiveInterval_Srflx_Returns300s()
    {
        var interval = IceUpgradeOptimizer.GetAdaptiveInterval("srflx", consecutiveRelayCount: 0, rttMs: 50);
        Assert.Equal(300_000, interval);
    }

    // ── IsRouteOptimal ─────────────────────────────────────────────────

    [Fact]
    public void IsRouteOptimal_Host_ReturnsTrue()
    {
        Assert.True(IceUpgradeOptimizer.IsRouteOptimal("host"));
    }

    [Fact]
    public void IsRouteOptimal_Srflx_ReturnsTrue()
    {
        Assert.True(IceUpgradeOptimizer.IsRouteOptimal("srflx"));
    }

    [Fact]
    public void IsRouteOptimal_Relay_ReturnsFalse()
    {
        Assert.False(IceUpgradeOptimizer.IsRouteOptimal("relay"));
    }

    [Fact]
    public void IsRouteOptimal_Unknown_ReturnsFalse()
    {
        Assert.False(IceUpgradeOptimizer.IsRouteOptimal("unknown"));
    }

    // ── RouteRank ──────────────────────────────────────────────────────

    [Fact]
    public void RouteRank_Host_IsBest()
    {
        Assert.Equal(1, IceUpgradeOptimizer.RouteRank("host"));
    }

    [Fact]
    public void RouteRank_Srflx_IsMiddle()
    {
        Assert.Equal(2, IceUpgradeOptimizer.RouteRank("srflx"));
    }

    [Fact]
    public void RouteRank_Relay_IsWorst()
    {
        Assert.Equal(3, IceUpgradeOptimizer.RouteRank("relay"));
    }

    [Fact]
    public void RouteRank_Host_BetterThan_Srflx()
    {
        Assert.True(IceUpgradeOptimizer.RouteRank("host") < IceUpgradeOptimizer.RouteRank("srflx"));
    }

    [Fact]
    public void RouteRank_Srflx_BetterThan_Relay()
    {
        Assert.True(IceUpgradeOptimizer.RouteRank("srflx") < IceUpgradeOptimizer.RouteRank("relay"));
    }

    [Fact]
    public void RouteRank_Unknown_IsWorstOfAll()
    {
        Assert.Equal(4, IceUpgradeOptimizer.RouteRank("unknown"));
    }

    // ── InferRouteType ─────────────────────────────────────────────────

    [Fact]
    public void InferRouteType_BothHost_ReturnsHost()
    {
        Assert.Equal("host", IceUpgradeOptimizer.InferRouteType("host", "host"));
    }

    [Fact]
    public void InferRouteType_OneRelay_ReturnsRelay()
    {
        Assert.Equal("relay", IceUpgradeOptimizer.InferRouteType("host", "relay"));
        Assert.Equal("relay", IceUpgradeOptimizer.InferRouteType("relay", "host"));
    }

    [Fact]
    public void InferRouteType_OneSrflx_ReturnsSrflx()
    {
        Assert.Equal("srflx", IceUpgradeOptimizer.InferRouteType("host", "srflx"));
        Assert.Equal("srflx", IceUpgradeOptimizer.InferRouteType("srflx", "host"));
    }

    [Fact]
    public void InferRouteType_OnePrflx_ReturnsSrflx()
    {
        Assert.Equal("srflx", IceUpgradeOptimizer.InferRouteType("host", "prflx"));
        Assert.Equal("srflx", IceUpgradeOptimizer.InferRouteType("prflx", "host"));
    }

    [Fact]
    public void InferRouteType_BothUnknown_ReturnsUnknown()
    {
        Assert.Equal("unknown", IceUpgradeOptimizer.InferRouteType("unknown", "unknown"));
    }

    // ── Start / Stop / Dispose lifecycle ───────────────────────────────

    [Fact]
    public void StartStop_DoesNotCrash()
    {
        using var optimizer = CreateTestOptimizer();
        optimizer.Start();
        optimizer.Stop();
    }

    [Fact]
    public void DoubleDispose_IsSafe()
    {
        var optimizer = CreateTestOptimizer();
        optimizer.Dispose();
        optimizer.Dispose(); // must not throw
    }

    [Fact]
    public void OnNetworkChanged_WhenNotStarted_DoesNotCrash()
    {
        using var optimizer = CreateTestOptimizer();
        optimizer.OnNetworkChanged(); // should not throw
    }

    [Fact]
    public void Start_AfterDispose_DoesNotCrash()
    {
        var optimizer = CreateTestOptimizer();
        optimizer.Dispose();
        optimizer.Start(); // should silently no-op
    }

    [Fact]
    public void DoubleStart_DoesNotCrash()
    {
        using var optimizer = CreateTestOptimizer();
        optimizer.Start();
        optimizer.Start(); // second start is a no-op
        optimizer.Stop();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static IceUpgradeOptimizer CreateTestOptimizer()
    {
        return new IceUpgradeOptimizer(
            getLocalIceType:  () => "host",
            getRemoteIceType: () => "host",
            getRttMs:         () => 10,
            restartIceAsync:  () => Task.CompletedTask,
            onLog:            _ => { });
    }
}

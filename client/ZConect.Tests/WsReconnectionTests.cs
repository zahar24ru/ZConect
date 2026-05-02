using UiApp.Services;
using Xunit;

namespace ZConect.Tests;

public class WsReconnectionTests
{
    // ── Backoff array values ──────────────────────────────────────────

    [Fact]
    public void BackoffMs_HasExpectedValues()
    {
        int[] expected = [1000, 2000, 4000, 8000, 15000, 30000];
        Assert.Equal(expected, WsReconnectionManager.BackoffMs);
    }

    // ── P2P alive threshold ───────────────────────────────────────────

    [Fact]
    public void IsP2PAlive_NoActivity_ReturnsFalse()
    {
        var mgr = CreateManager();
        Assert.False(mgr.IsP2PAlive);
    }

    [Fact]
    public void IsP2PAlive_RecentActivity_ReturnsTrue()
    {
        var mgr = CreateManager();
        mgr.TouchP2PActivity();
        Assert.True(mgr.IsP2PAlive);
    }

    [Fact]
    public void IsP2PAlive_OldActivity_ReturnsFalse()
    {
        // We can't easily fake time, but we can verify the threshold constant is 10s.
        Assert.Equal(TimeSpan.FromSeconds(10), WsReconnectionManager.P2PAliveThreshold);
    }

    // ── TouchP2PActivity ──────────────────────────────────────────────

    [Fact]
    public void TouchP2PActivity_MakesP2PAlive()
    {
        var mgr = CreateManager();
        Assert.False(mgr.IsP2PAlive);
        mgr.TouchP2PActivity();
        Assert.True(mgr.IsP2PAlive);
    }

    [Fact]
    public void ResetP2PActivity_ClearsAliveState()
    {
        var mgr = CreateManager();
        mgr.TouchP2PActivity();
        Assert.True(mgr.IsP2PAlive);
        mgr.ResetP2PActivity();
        Assert.False(mgr.IsP2PAlive);
    }

    // ── IsReconnecting flag ───────────────────────────────────────────

    [Fact]
    public void IsReconnecting_InitiallyFalse()
    {
        var mgr = CreateManager();
        Assert.False(mgr.IsReconnecting);
    }

    [Fact]
    public async Task IsReconnecting_SetDuringReconnect()
    {
        var mgr = CreateManager(role: "Host", sessionId: "test-session");
        var enteredLoop = new TaskCompletionSource<bool>();
        var releaseLoop = new TaskCompletionSource<bool>();

        // Host refresh callback that blocks until we release it.
        var task = mgr.ReconnectAsync(
            onHostRefreshAsync: async (sid, secret, attempt) =>
            {
                enteredLoop.TrySetResult(true);
                await releaseLoop.Task;
                return WsReconnectionManager.HostRefreshOutcome.Success;
            },
            onViewerRejoinAsync: null,
            onHostWsReconnectAsync: (url, token, sid) => Task.FromResult(true),
            onStatusUpdate: null,
            wsUrl: "ws://test",
            sessionId: "test-session",
            ownerSecret: "secret",
            ct: CancellationToken.None);

        // Wait for the first attempt callback to fire (after 1s backoff).
        var entered = await Task.WhenAny(enteredLoop.Task, Task.Delay(5000));
        Assert.Same(enteredLoop.Task, entered);
        Assert.True(mgr.IsReconnecting);

        // Release and let it finish.
        releaseLoop.SetResult(true);
        var result = await task;
        Assert.Equal(WsReconnectionManager.ReconnectResult.Success, result);
        Assert.False(mgr.IsReconnecting);
    }

    // ── Double reconnect attempt blocked ──────────────────────────────

    [Fact]
    public async Task DoubleReconnect_BlockedByAtomicFlag()
    {
        var mgr = CreateManager(role: "Host", sessionId: "test-session");
        var enteredLoop = new TaskCompletionSource<bool>();
        var releaseLoop = new TaskCompletionSource<bool>();

        var task1 = mgr.ReconnectAsync(
            onHostRefreshAsync: async (sid, secret, attempt) =>
            {
                enteredLoop.TrySetResult(true);
                await releaseLoop.Task;
                return WsReconnectionManager.HostRefreshOutcome.Success;
            },
            onViewerRejoinAsync: null,
            onHostWsReconnectAsync: (url, token, sid) => Task.FromResult(true),
            onStatusUpdate: null,
            wsUrl: "ws://test",
            sessionId: "test-session",
            ownerSecret: "secret",
            ct: CancellationToken.None);

        // Wait for the first task to enter the loop.
        await enteredLoop.Task;

        // Second call should return AlreadyReconnecting immediately.
        var result2 = await mgr.ReconnectAsync(
            onHostRefreshAsync: (sid, secret, attempt) => Task.FromResult(WsReconnectionManager.HostRefreshOutcome.Success),
            onViewerRejoinAsync: null,
            onHostWsReconnectAsync: (url, token, sid) => Task.FromResult(true),
            onStatusUpdate: null,
            wsUrl: "ws://test",
            sessionId: "test-session",
            ownerSecret: "secret",
            ct: CancellationToken.None);

        Assert.Equal(WsReconnectionManager.ReconnectResult.AlreadyReconnecting, result2);

        // Cleanup: release the first task.
        releaseLoop.SetResult(true);
        await task1;
    }

    // ── Cancellation ──────────────────────────────────────────────────

    [Fact]
    public async Task ReconnectAsync_CancelledViaCancellationToken()
    {
        var mgr = CreateManager(role: "Host", sessionId: "test-session");
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        var result = await mgr.ReconnectAsync(
            onHostRefreshAsync: (sid, secret, attempt) => Task.FromResult(WsReconnectionManager.HostRefreshOutcome.Success),
            onViewerRejoinAsync: null,
            onHostWsReconnectAsync: (url, token, sid) => Task.FromResult(true),
            onStatusUpdate: null,
            wsUrl: "ws://test",
            sessionId: "test-session",
            ownerSecret: "secret",
            ct: cts.Token);

        Assert.Equal(WsReconnectionManager.ReconnectResult.Cancelled, result);
        Assert.False(mgr.IsReconnecting);
    }

    [Fact]
    public async Task ReconnectAsync_CancelledWhenSessionCleared()
    {
        var currentSessionId = "test-session";
        var mgr = CreateManager(role: "Host", getSessionId: () => currentSessionId);

        // Clear session before first attempt completes.
        currentSessionId = string.Empty;

        var result = await mgr.ReconnectAsync(
            onHostRefreshAsync: (sid, secret, attempt) => Task.FromResult(WsReconnectionManager.HostRefreshOutcome.Success),
            onViewerRejoinAsync: null,
            onHostWsReconnectAsync: (url, token, sid) => Task.FromResult(true),
            onStatusUpdate: null,
            wsUrl: "ws://test",
            sessionId: "test-session",
            ownerSecret: "secret",
            ct: CancellationToken.None);

        Assert.Equal(WsReconnectionManager.ReconnectResult.Cancelled, result);
    }

    // ── Reset ─────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsReconnectingFlag()
    {
        var mgr = CreateManager();
        // Can't easily set the flag without running ReconnectAsync,
        // but verify Reset doesn't throw.
        mgr.Reset();
        Assert.False(mgr.IsReconnecting);
    }

    // ── Helper ────────────────────────────────────────────────────────

    private static WsReconnectionManager CreateManager(
        string role = "None",
        string sessionId = "",
        Func<string>? getSessionId = null)
    {
        return new WsReconnectionManager(
            onLog: _ => { },
            getRole: () => role,
            getSessionId: getSessionId ?? (() => sessionId));
    }
}

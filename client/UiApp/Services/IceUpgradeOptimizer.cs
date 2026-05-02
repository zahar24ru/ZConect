using System.Collections.Concurrent;
using System.Net.NetworkInformation;

namespace UiApp.Services;

/// <summary>
/// Monitors the active ICE route and triggers ICE restarts when a more direct
/// path may be available (e.g. after a network change or while stuck on relay).
/// Extracted from MainViewModel to keep connection-management concerns separate.
/// </summary>
public sealed class IceUpgradeOptimizer : IDisposable
{
    // ── Dependencies (injected via constructor) ────────────────────────
    private readonly Func<string> _getLocalIceType;
    private readonly Func<string> _getRemoteIceType;
    private readonly Func<int>    _getRttMs;
    private readonly Func<Task>   _restartIceAsync;
    private readonly Action<string> _onLog;

    // ── State ──────────────────────────────────────────────────────────
    private volatile int _running;
    private readonly ConcurrentQueue<byte> _networkChangedSignal = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public IceUpgradeOptimizer(
        Func<string> getLocalIceType,
        Func<string> getRemoteIceType,
        Func<int>    getRttMs,
        Func<Task>   restartIceAsync,
        Action<string> onLog)
    {
        _getLocalIceType  = getLocalIceType  ?? throw new ArgumentNullException(nameof(getLocalIceType));
        _getRemoteIceType = getRemoteIceType ?? throw new ArgumentNullException(nameof(getRemoteIceType));
        _getRttMs         = getRttMs         ?? throw new ArgumentNullException(nameof(getRttMs));
        _restartIceAsync  = restartIceAsync  ?? throw new ArgumentNullException(nameof(restartIceAsync));
        _onLog            = onLog            ?? throw new ArgumentNullException(nameof(onLog));
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Start the background ICE upgrade monitor loop.</summary>
    public void Start(CancellationToken externalCt = default)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var token = _cts.Token;

        NetworkChange.NetworkAddressChanged      += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged  += OnNetworkAvailabilityChanged;

        _ = Task.Run(() => MonitorLoopAsync(token));
    }

    /// <summary>Stop monitoring and unsubscribe from network events.</summary>
    public void Stop()
    {
        Interlocked.Exchange(ref _running, 0);
        try { _cts?.Cancel(); } catch { /* ignore */ }
        UnsubscribeNetworkEvents();
    }

    /// <summary>Signal the monitor that the network environment has changed.</summary>
    public void OnNetworkChanged()
    {
        _networkChangedSignal.Enqueue(1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    // ── Internal helpers exposed for unit testing ──────────────────────

    internal static bool IsRouteOptimal(string route) =>
        string.Equals(route, "host",  StringComparison.OrdinalIgnoreCase)
     || string.Equals(route, "srflx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower = better. Used for upgrade/degrade detection.</summary>
    internal static int RouteRank(string route) => route.ToLowerInvariant() switch
    {
        "host"  => 1,
        "srflx" => 2,
        "relay" => 3,
        _       => 4
    };

    /// <summary>Adaptive interval: relay -> short intervals, good route -> long/stop.</summary>
    internal static int GetAdaptiveInterval(string route, int consecutiveRelayCount, double rttMs)
    {
        if (IsRouteOptimal(route))
            return 300_000; // 5 min — just monitor for degradation

        // Relay: start aggressive (15s), back off with failures.
        return consecutiveRelayCount switch
        {
            0 => 15_000,   // first check after initial 10s delay
            1 => 30_000,
            2 => 60_000,
            3 => 120_000,
            _ => 300_000   // 5 min — mostly waiting for network changes
        };
    }

    internal static string InferRouteType(string localType, string remoteType)
    {
        if (string.Equals(localType, "relay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "relay", StringComparison.OrdinalIgnoreCase))
        {
            return "relay";
        }
        if (string.Equals(localType, "srflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "srflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(localType, "prflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "prflx", StringComparison.OrdinalIgnoreCase))
        {
            return "srflx";
        }
        if (string.Equals(localType, "host", StringComparison.OrdinalIgnoreCase)
            && string.Equals(remoteType, "host", StringComparison.OrdinalIgnoreCase))
        {
            return "host";
        }
        return "unknown";
    }

    // ── Private implementation ─────────────────────────────────────────

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _onLog("network_address_changed_detected");
        _networkChangedSignal.Enqueue(1);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _onLog("network_became_available");
            _networkChangedSignal.Enqueue(1);
        }
    }

    private void UnsubscribeNetworkEvents()
    {
        try { NetworkChange.NetworkAddressChanged      -= OnNetworkAddressChanged; } catch { }
        try { NetworkChange.NetworkAvailabilityChanged  -= OnNetworkAvailabilityChanged; } catch { }
    }

    /// <summary>
    /// Unified ICE upgrade monitor. Triggers ICE restart when:
    /// 1) Timer fires (adaptive intervals based on current route quality)
    /// 2) Network interface changes (Ethernet plugged, VPN connected, WiFi switched)
    /// 3) RTT drops significantly (suggests ICE agent found a better path internally)
    ///
    /// ICE restart happens on the EXISTING PeerConnection — zero disruption.
    /// If restart degrades the route, it triggers another restart to recover.
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            // Initial stabilization delay.
            await Task.Delay(10_000, ct);

            var consecutiveRelayCount = 0;
            var routeBeforeRestart = "unknown";

            while (!ct.IsCancellationRequested && _running == 1)
            {
                var currentRoute = InferRouteType(_getLocalIceType(), _getRemoteIceType());
                var currentRtt = _getRttMs();

                // Determine next check interval based on current state.
                var intervalMs = GetAdaptiveInterval(currentRoute, consecutiveRelayCount, currentRtt);

                // Wait for interval OR network change event (whichever comes first).
                var waited = 0;
                var networkTriggered = false;
                while (waited < intervalMs && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct);
                    waited += 500;

                    // Check for network change signal.
                    if (_networkChangedSignal.TryDequeue(out _))
                    {
                        // Drain any additional signals.
                        while (_networkChangedSignal.TryDequeue(out _)) { }
                        // Brief debounce — network events come in bursts.
                        await Task.Delay(3000, ct);
                        networkTriggered = true;
                        break;
                    }
                }

                if (ct.IsCancellationRequested || _running != 1) break;

                // Re-check route — might have changed while waiting.
                currentRoute = InferRouteType(_getLocalIceType(), _getRemoteIceType());
                if (IsRouteOptimal(currentRoute))
                {
                    var currentRttCheck = _getRttMs();
                    if (!networkTriggered || currentRttCheck < 100)
                    {
                        consecutiveRelayCount = 0;
                        if (networkTriggered)
                            _onLog($"skipped_restart_optimal_route_{currentRoute}_rtt={currentRttCheck}ms");
                        continue;
                    }
                }

                // Attempt ICE restart.
                routeBeforeRestart = currentRoute;
                var rttBeforeRestart = _getRttMs();
                var trigger = networkTriggered ? "network_change" : $"timer_{intervalMs}ms";
                _onLog($"attempting_restart_trigger={trigger}_route={currentRoute}_rtt={rttBeforeRestart}ms");

                try
                {
                    await _restartIceAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _onLog("restart_failed_" + ex.Message);
                    consecutiveRelayCount++;
                    continue;
                }

                // Wait for ICE to settle (up to 10s).
                string? newRoute = null;
                for (var i = 0; i < 50; i++)
                {
                    await Task.Delay(200, ct);
                    var r = InferRouteType(_getLocalIceType(), _getRemoteIceType());
                    if (!string.Equals(r, "unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(1000, ct);
                        newRoute = InferRouteType(_getLocalIceType(), _getRemoteIceType());
                        break;
                    }
                }
                newRoute ??= InferRouteType(_getLocalIceType(), _getRemoteIceType());

                var upgraded = RouteRank(newRoute) < RouteRank(routeBeforeRestart);
                var degraded = RouteRank(newRoute) > RouteRank(routeBeforeRestart);

                if (upgraded)
                {
                    consecutiveRelayCount = 0;
                    _onLog($"upgrade_success_{routeBeforeRestart}->{newRoute}");
                }
                else if (degraded)
                {
                    _onLog($"degraded_{routeBeforeRestart}->{newRoute}_retrying_recovery");
                    try { await _restartIceAsync(); } catch { /* recovery best-effort */ }
                    consecutiveRelayCount++;
                }
                else
                {
                    consecutiveRelayCount++;
                    _onLog($"no_change_still_{newRoute}_count={consecutiveRelayCount}");
                }

                // Stop trying if we've been on relay for too long with no success.
                if (consecutiveRelayCount >= 10 && !networkTriggered)
                {
                    _onLog("giving_up_timer_upgrades_will_react_to_network_changes_only");
                    consecutiveRelayCount = 10; // cap to keep interval stable
                }
            }
        }
        catch (OperationCanceledException) { /* session ended */ }
        catch (Exception ex)
        {
            _onLog("monitor_error_" + ex.Message);
        }
        finally
        {
            Stop();
        }
    }
}

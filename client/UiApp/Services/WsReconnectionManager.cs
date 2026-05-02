namespace UiApp.Services;

/// <summary>
/// Manages WebSocket reconnection with exponential backoff.
/// Extracted from MainViewModel to keep reconnection state-machine logic separate
/// from the actual signaling and API operations (which are injected as callbacks).
/// </summary>
public sealed class WsReconnectionManager
{
    /// <summary>Backoff delays in milliseconds for successive reconnect attempts.
    /// Clamp'ится на 30s для attempts > BackoffMs.Length.</summary>
    public static readonly int[] BackoffMs = [1000, 2000, 4000, 8000, 15000, 30000];

    /// <summary>Maximum reconnect attempts when NetworkError. Clamps at 30s
    /// backoff → total wait ~10 мин (1+2+4+8+15+30*17 = ~9 мин). Allows
    /// recovery после 10-min outage интернета у user'а.
    ///
    /// SessionGone (HTTP 404) пропускает счётчик и сразу HostNeedsRecreate.
    /// Так что только network/server 5xx используют full 20 attempts.</summary>
    public const int MaxAttempts = 20;

    /// <summary>
    /// If no P2P activity is observed for this duration, the connection is considered dead.
    /// </summary>
    public static readonly TimeSpan P2PAliveThreshold = TimeSpan.FromSeconds(10);

    // ── Dependencies (injected via constructor) ──────────────────────────
    private readonly Action<string> _onLog;
    private readonly Func<string> _getRole;
    private readonly Func<string> _getSessionId;

    // ── State ────────────────────────────────────────────────────────────
    private int _reconnecting; // 0 = idle, 1 = reconnecting (atomic via Interlocked)
    private long _lastP2PActivityTicks;

    public WsReconnectionManager(
        Action<string> onLog,
        Func<string> getRole,
        Func<string> getSessionId)
    {
        _onLog = onLog ?? throw new ArgumentNullException(nameof(onLog));
        _getRole = getRole ?? throw new ArgumentNullException(nameof(getRole));
        _getSessionId = getSessionId ?? throw new ArgumentNullException(nameof(getSessionId));
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>True when a reconnect loop is already running (atomic check).</summary>
    public bool IsReconnecting => Interlocked.CompareExchange(ref _reconnecting, 0, 0) != 0;

    /// <summary>
    /// True when P2P WebRTC activity was observed within <see cref="P2PAliveThreshold"/>.
    /// </summary>
    public bool IsP2PAlive
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastP2PActivityTicks);
            if (ticks == 0) return false;
            return (DateTime.UtcNow.Ticks - ticks) < P2PAliveThreshold.Ticks;
        }
    }

    /// <summary>Mark P2P as active (call on successful video frame or DC message).</summary>
    public void TouchP2PActivity() => Interlocked.Exchange(ref _lastP2PActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>Reset P2P activity timestamp (e.g. on full connection cleanup).</summary>
    public void ResetP2PActivity() => Interlocked.Exchange(ref _lastP2PActivityTicks, 0);

    /// <summary>Clear the reconnecting flag (e.g. on full connection cleanup).</summary>
    public void Reset() => Interlocked.Exchange(ref _reconnecting, 0);

    /// <summary>
    /// Result of a single reconnect loop invocation.
    /// </summary>
    public enum ReconnectResult
    {
        /// <summary>Successfully reconnected.</summary>
        Success,
        /// <summary>Already reconnecting — duplicate call blocked.</summary>
        AlreadyReconnecting,
        /// <summary>All attempts exhausted, P2P is still alive.</summary>
        GaveUpP2PAlive,
        /// <summary>All attempts exhausted, P2P is dead.</summary>
        GaveUpP2PDead,
        /// <summary>Host: all refresh attempts failed, P2P dead — caller should recreate session.</summary>
        HostNeedsRecreate,
        /// <summary>Loop was cancelled or session cleared by user.</summary>
        Cancelled
    }

    /// <summary>
    /// Run the reconnect loop with exponential backoff.
    /// The actual API calls (host refresh, viewer rejoin, WS reconnect) are provided as callbacks
    /// so this class stays decoupled from signaling and session management.
    /// </summary>
    /// <param name="onHostRefreshAsync">
    /// Host path: refresh session to get fresh WS token. Returns true on success.
    /// Called with (sessionId, ownerSecret, attempt).
    /// </param>
    /// <param name="onViewerRejoinAsync">
    /// Viewer path: re-join session for a full reconnect. Returns true on success.
    /// </param>
    /// <param name="onHostWsReconnectAsync">
    /// Host path: perform the actual WS-only reconnect with the fresh token.
    /// Called with (wsUrl, sessionId, ownerSecret). The freshToken is captured by
    /// caller's closure (set inside onHostRefreshAsync на success) — НЕ передаётся
    /// через эти аргументы. Возвращает true при успехе.
    ///
    /// Note (audit M-2 fix 2026-04-25): раньше XML говорил "(wsUrl, freshToken,
    /// sessionId)" но код передавал (wsUrl, sessionId, ownerSecret). Реальный flow
    /// использует closure для freshToken — параметры остались для legacy compatibility,
    /// 3-й (ownerSecret) сейчас dead. Не меняем signature чтобы не ломать callers,
    /// но docs corrected.
    /// </param>
    /// <param name="onStatusUpdate">
    /// Called to update UI status text. Params: (message, isP2PAlive).
    /// </param>
    /// <param name="ct">Cancellation token (tied to app lifetime).</param>
    /// <summary>Refresh result возвращённый host callback'ом.
    /// Различает "network down" (retry долго) vs "session gone" (немедленно recreate).</summary>
    public enum HostRefreshOutcome
    {
        Success,        // refresh OK → можно reconnect'ить WS
        NetworkError,   // нет интернета / timeout → retry с backoff
        SessionGone,    // HTTP 404/403/410 — session стёрта на сервере → recreate immediately
        ServerError,    // HTTP 5xx → retry осторожно
    }

    public async Task<ReconnectResult> ReconnectAsync(
        Func<string, string, int, Task<HostRefreshOutcome>>? onHostRefreshAsync,
        Func<Task<bool>>? onViewerRejoinAsync,
        Func<string, string, string, Task<bool>>? onHostWsReconnectAsync,
        Action<string, bool>? onStatusUpdate,
        string wsUrl,
        string sessionId,
        string ownerSecret,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
            return ReconnectResult.AlreadyReconnecting;

        try
        {
            var role = _getRole();

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested || string.IsNullOrEmpty(_getSessionId()))
                    return ReconnectResult.Cancelled;

                // For host: check that role hasn't changed.
                if (role == "Host" && _getRole() != "Host")
                    return ReconnectResult.Cancelled;

                var delay = BackoffMs[Math.Min(attempt, BackoffMs.Length - 1)];
                var p2pAlive = IsP2PAlive;

                _onLog($"ws_reconnect_attempt_{attempt + 1}_delay_{delay}ms_role_{role}_p2p_{p2pAlive}");
                onStatusUpdate?.Invoke(
                    p2pAlive
                        ? $"P2P активен (WS переподключение {attempt + 1}/{MaxAttempts})"
                        : $"Переподключение ({attempt + 1}/{MaxAttempts})...",
                    p2pAlive);

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return ReconnectResult.Cancelled; }

                if (role == "Host" && _getRole() != "Host")
                    return ReconnectResult.Cancelled;

                try
                {
                    if (role == "Viewer")
                    {
                        if (onViewerRejoinAsync is not null && await onViewerRejoinAsync())
                        {
                            _onLog($"viewer_full_reconnect_success_attempt_{attempt + 1}");
                            return ReconnectResult.Success;
                        }
                        _onLog("ws_reconnect_viewer_rejoin_failed");
                        continue;
                    }

                    // Host path: refresh session for fresh WS token + detailed outcome.
                    var outcome = onHostRefreshAsync is not null
                        ? await onHostRefreshAsync(sessionId, ownerSecret, attempt)
                        : HostRefreshOutcome.ServerError;

                    // SessionGone: server рестартанул или session expired/wiped.
                    // Нет смысла retry — сразу recreate новую session.
                    if (outcome == HostRefreshOutcome.SessionGone)
                    {
                        _onLog($"ws_reconnect_session_gone_fast_path_attempt_{attempt + 1}");
                        return ReconnectResult.HostNeedsRecreate;
                    }

                    if (outcome == HostRefreshOutcome.NetworkError || outcome == HostRefreshOutcome.ServerError)
                    {
                        _onLog($"ws_reconnect_refresh_{outcome.ToString().ToLower()}_attempt_{attempt + 1}_{MaxAttempts}");
                        if (attempt >= MaxAttempts - 1)
                        {
                            if (IsP2PAlive)
                            {
                                _onLog("ws_reconnect_host_gave_up_but_p2p_alive");
                                return ReconnectResult.GaveUpP2PAlive;
                            }
                            // После ~10 мин — network всё ещё down, session уже expired на сервере.
                            // Recreate попытается и может упасть тоже — но хотя бы не висим.
                            _onLog("ws_reconnect_host_all_attempts_failed_recreating_session");
                            return ReconnectResult.HostNeedsRecreate;
                        }
                        continue;
                    }

                    // Success — actual WS reconnect.
                    if (onHostWsReconnectAsync is not null
                        && await onHostWsReconnectAsync(wsUrl, sessionId, ownerSecret))
                    {
                        _onLog($"ws_reconnect_success_attempt_{attempt + 1}");
                        return ReconnectResult.Success;
                    }
                }
                catch (Exception ex)
                {
                    _onLog($"ws_reconnect_failed_{ex.Message}");
                }
            }

            _onLog("ws_reconnect_gave_up");
            return IsP2PAlive ? ReconnectResult.GaveUpP2PAlive : ReconnectResult.GaveUpP2PDead;
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }
}

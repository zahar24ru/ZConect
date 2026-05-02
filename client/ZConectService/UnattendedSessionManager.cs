using SessionClient;

namespace ZConectService;

/// <summary>
/// Manages persistent unattended access sessions.
/// Creates a session on service start, keeps it alive with periodic refresh,
/// and preserves login/password codes across reboots via machine_id + device_secret.
/// </summary>
public sealed class UnattendedSessionManager
{
    private readonly ServiceLogger _log;
    private readonly ServiceConfig _config;
    private readonly SessionApiClient _apiClient;

    private string _sessionId = "";
    private string _ownerSecret = "";
    private string _loginCode = "";
    private string _passCode = "";
    private string _wsUrl = "";
    private string _wsToken = "";
    private DateTime _lastRefresh = DateTime.MinValue;
    private int _consecutiveRefreshFailures;

    /// <summary>Current session codes (empty if no session).</summary>
    public string LoginCode => _loginCode;
    public string PassCode => _passCode;
    public string SessionId => _sessionId;
    public string OwnerSecret => _ownerSecret;
    public string WsUrl => _wsUrl;
    public string WsToken => _wsToken;
    public bool HasSession => !string.IsNullOrEmpty(_sessionId);

    private const int RefreshIntervalSec = 120; // refresh every 2 min to keep session alive

    public UnattendedSessionManager(ServiceLogger log, ServiceConfig config)
    {
        _log = log;
        _config = config;
        // Audit fix 2026-04-24 (M2): timeout 30s + size cap. См. MainWindow.xaml.cs notes.
        _apiClient = new SessionApiClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 },
            msg => log.Debug("API", msg));
    }

    /// <summary>Create or resume unattended session with retry on network failure.</summary>
    public async Task StartSessionAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.SignalingUrl))
        {
            _log.Warn("Unattended", "no_signaling_url_configured");
            return;
        }

        _log.Info("Unattended", $"creating_session machine_id={_config.MachineId}");

        // Retry with backoff: 5s → 10s → 30s → 60s, up to 10 minutes (handles boot without network).
        int[] backoffMs = [5000, 10000, 30000, 60000];
        var startTime = DateTime.UtcNow;

        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                var deviceSecret = !string.IsNullOrEmpty(_config.DeviceSecret) ? _config.DeviceSecret : null;
                if (attempt == 1)
                    _log.Info("Unattended", $"has_device_secret={deviceSecret is not null}");

                var response = await _apiClient.CreateSessionAsync(
                    _config.SignalingUrl,
                    requestUnattended: true,
                    expiresInSec: 86400,
                    machineId: _config.MachineId,
                    deviceSecret: deviceSecret,
                    ct: ct);

                if (response is null)
                    throw new Exception("CreateSessionAsync returned null");

                _sessionId = response.SessionId;
                _ownerSecret = response.OwnerSecret;
                _loginCode = response.LoginCode;
                _passCode = response.PassCode;
                _wsUrl = response.WsUrl;
                _wsToken = response.WsToken;
                _lastRefresh = DateTime.UtcNow;

                if (!string.IsNullOrEmpty(response.DeviceSecret))
                {
                    _config.DeviceSecret = response.DeviceSecret;
                    _config.Save();
                    _log.Info("Unattended", "device_secret_saved");
                }

                _log.Info("Unattended", $"session_created id={_sessionId} login=****{_loginCode[^4..]} pass=****{_passCode[^4..]}");
                return; // success
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - startTime).TotalMinutes > 10)
                {
                    _log.Error("Unattended", "create_session_abandoned_after_10min", ex.Message);
                    return;
                }
                var waitMs = backoffMs[Math.Min(attempt - 1, backoffMs.Length - 1)];
                _log.Warn("Unattended", $"create_session_failed attempt={attempt} retry_in={waitMs}ms: {ex.Message}");
                try { await Task.Delay(waitMs, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Call periodically (every tick) to refresh the session before TTL expires.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!HasSession) return;
        if ((DateTime.UtcNow - _lastRefresh).TotalSeconds < RefreshIntervalSec) return;

        try
        {
            var response = await _apiClient.RefreshSessionAsync(
                _config.SignalingUrl,
                _sessionId,
                _ownerSecret,
                expiresInSec: 86400, // extend 24h
                regeneratePass: false, // keep same codes!
                ct: ct);

            if (response is not null)
            {
                _consecutiveRefreshFailures = 0;
                _lastRefresh = DateTime.UtcNow;
                _loginCode = response.LoginCode;
                _passCode = response.PassCode;
                _log.Debug("Unattended", $"session_refreshed login=****{_loginCode[^4..]}");
            }
            else
            {
                _consecutiveRefreshFailures++;
                _log.Warn("Unattended", $"session_refresh_failed consecutive={_consecutiveRefreshFailures}");

                // Only recreate after 3 consecutive failures (not on first network blip).
                if (_consecutiveRefreshFailures >= 3)
                {
                    _log.Warn("Unattended", "recreating_session_after_3_failures");
                    _sessionId = "";
                    _consecutiveRefreshFailures = 0;
                    await StartSessionAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            _consecutiveRefreshFailures++;
            _log.Warn("Unattended", $"refresh_exception consecutive={_consecutiveRefreshFailures}: {ex.Message}");

            if (_consecutiveRefreshFailures >= 3)
            {
                _log.Warn("Unattended", "recreating_session_after_3_exceptions");
                _sessionId = "";
                _consecutiveRefreshFailures = 0;
                await StartSessionAsync(ct);
            }
        }
    }

    /// <summary>On service stop: keep session alive on server (don't close it).
    /// Session survives via TTL and will be reused on next start via machine_id + device_secret.</summary>
    public Task StopSessionAsync()
    {
        if (HasSession)
        {
            _log.Info("Unattended", $"service_stopping_session_kept_alive id={_sessionId} login=****{_loginCode[^4..]}");
        }
        _sessionId = "";
        _loginCode = "";
        _passCode = "";
        return Task.CompletedTask;
    }
}

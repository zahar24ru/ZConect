using System.Net.Http.Json;
using System.Text.Json;

namespace SessionClient;

public sealed class SessionApiClient
{
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _onLog;

    /// <summary>Optional log callback for diagnosing API failures (network vs auth vs timeout).</summary>
    public SessionApiClient(HttpClient httpClient, Action<string>? onLog = null)
    {
        _onLog = onLog;
        _httpClient = httpClient;
    }

    /// <param name="expiresInSec">Optional TTL in seconds for unattended long-lived session (only when requestUnattended). Server caps at 7 days.</param>
    /// <param name="machineId">Persistent machine identifier. Server reuses an active session for the same machine.</param>
    /// <summary>Legacy thin wrapper — возвращает только Response. Для back-compat.
    /// Новые call sites должны использовать <see cref="CreateSessionAsyncDetailed"/>
    /// чтобы различать Banned/Maintenance/NetworkError и показывать proper user messages.</summary>
    public async Task<CreateSessionResponse?> CreateSessionAsync(string baseUrl, bool requestUnattended, int? expiresInSec = null, string? machineId = null, string? deviceSecret = null, CancellationToken ct = default)
    {
        var result = await CreateSessionAsyncDetailed(baseUrl, requestUnattended, expiresInSec, machineId, deviceSecret, ct);
        return result.Response;
    }

    public async Task<CreateSessionResult> CreateSessionAsyncDetailed(string baseUrl, bool requestUnattended, int? expiresInSec = null, string? machineId = null, string? deviceSecret = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object>
        {
            ["request_unattended"] = requestUnattended
        };
        if (expiresInSec is > 0)
        {
            payload["expires_in_sec"] = expiresInSec.Value;
        }
        if (!string.IsNullOrWhiteSpace(machineId))
        {
            payload["machine_id"] = machineId;
        }
        if (!string.IsNullOrWhiteSpace(deviceSecret))
        {
            payload["device_secret"] = deviceSecret;
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/create", payload, ct);
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                _onLog?.Invoke($"create_http_{code}");
                var errMsg = await TryReadErrorMessage(response, ct);
                var status = code switch
                {
                    403 => CreateSessionStatus.Banned,
                    503 => CreateSessionStatus.Maintenance,
                    >= 500 => CreateSessionStatus.ServerError,
                    _ => CreateSessionStatus.ServerError,
                };
                return new CreateSessionResult { Status = status, HttpStatus = code, ErrorMessage = errMsg };
            }

            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (json is null)
                return new CreateSessionResult { Status = CreateSessionStatus.InvalidResponse, HttpStatus = code };

            var resp = new CreateSessionResponse
            {
                SessionId = json.TryGetValue("session_id", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
                LoginCode = json.TryGetValue("login_code", out var lc) ? lc?.ToString() ?? string.Empty : string.Empty,
                PassCode = json.TryGetValue("pass_code", out var pc) ? pc?.ToString() ?? string.Empty : string.Empty,
                ExpiresInSec = TryParseInt(json, "expires_in_sec"),
                WsUrl = json.TryGetValue("ws_url", out var wsu) ? wsu?.ToString() ?? string.Empty : string.Empty,
                WsToken = json.TryGetValue("ws_token", out var token) ? token?.ToString() ?? string.Empty : string.Empty,
                OwnerSecret = json.TryGetValue("owner_secret", out var os) ? os?.ToString() ?? string.Empty : string.Empty,
                DeviceSecret = json.TryGetValue("device_secret", out var ds) ? ds?.ToString() ?? string.Empty : string.Empty,
                TurnServers = ParseTurnServers(json)
            };
            return new CreateSessionResult { Status = CreateSessionStatus.Success, Response = resp, HttpStatus = code };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _onLog?.Invoke($"api_error_{ex.GetType().Name}:{ex.Message}");
            return new CreateSessionResult { Status = CreateSessionStatus.NetworkError, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Legacy thin wrapper. Новые call sites используют <see cref="JoinSessionAsyncDetailed"/>
    /// чтобы различать Banned/SessionLocked/SessionBlocked и показывать proper user message.</summary>
    public async Task<JoinSessionResponse?> JoinSessionAsync(string baseUrl, string loginCode, string passCode, CancellationToken ct = default)
    {
        var result = await JoinSessionAsyncDetailed(baseUrl, loginCode, passCode, ct);
        return result.Response;
    }

    public async Task<JoinSessionResult> JoinSessionAsyncDetailed(string baseUrl, string loginCode, string passCode, CancellationToken ct = default)
    {
        var payload = new
        {
            login_code = loginCode,
            pass_code = passCode
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/join", payload, ct);
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                _onLog?.Invoke($"join_http_{code}");
                var errMsg = await TryReadErrorMessage(response, ct);
                // Retry-After header имеет приоритет над body — standard HTTP, проще парсится.
                int retryAfter = 0;
                if (response.Headers.TryGetValues("Retry-After", out var retryVals))
                {
                    foreach (var v in retryVals)
                    {
                        if (int.TryParse(v, out var sec) && sec > 0) { retryAfter = sec; break; }
                    }
                }
                // Fallback: parse из body {"retry_after_sec": N} если header отсутствует.
                if (retryAfter == 0)
                {
                    retryAfter = await TryReadRetryAfterBody(response, ct);
                }
                var status = code switch
                {
                    401 => JoinSessionStatus.InvalidCredentials,
                    403 => JoinSessionStatus.Banned,
                    423 => JoinSessionStatus.SessionBlocked,
                    429 => JoinSessionStatus.SessionLocked,
                    >= 500 => JoinSessionStatus.ServerError,
                    _ => JoinSessionStatus.InvalidCredentials, // default catch-all для unexpected codes
                };
                return new JoinSessionResult { Status = status, HttpStatus = code, RetryAfterSec = retryAfter, ErrorMessage = errMsg };
            }

            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (json is null)
                return new JoinSessionResult { Status = JoinSessionStatus.ServerError, HttpStatus = code };

            var resp = new JoinSessionResponse
            {
                SessionId = json.TryGetValue("session_id", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
                RequireConfirm = TryParseBool(json, "require_confirm"),
                State = json.TryGetValue("state", out var state) ? state?.ToString() ?? string.Empty : string.Empty,
                WsUrl = json.TryGetValue("ws_url", out var wsu) ? wsu?.ToString() ?? string.Empty : string.Empty,
                WsToken = json.TryGetValue("ws_token", out var token) ? token?.ToString() ?? string.Empty : string.Empty,
                TurnServers = ParseTurnServers(json)
            };
            return new JoinSessionResult { Status = JoinSessionStatus.Success, Response = resp, HttpStatus = code };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _onLog?.Invoke($"api_error_{ex.GetType().Name}:{ex.Message}");
            return new JoinSessionResult { Status = JoinSessionStatus.NetworkError, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Read "error" field from error-response body. Returns null on parse fail.</summary>
    private static async Task<string?> TryReadErrorMessage(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var dict = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (dict is not null && dict.TryGetValue("error", out var e))
                return e?.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>Parse retry_after_sec из error response body.</summary>
    private static async Task<int> TryReadRetryAfterBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var dict = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (dict is not null && dict.TryGetValue("retry_after_sec", out var v) && v is not null)
            {
                if (int.TryParse(v.ToString(), out var n) && n > 0) return n;
            }
        }
        catch { }
        return 0;
    }

    public async Task<bool> CloseSessionAsync(string baseUrl, string sessionId, string ownerSecret = "", CancellationToken ct = default)
    {
        var payload = new { session_id = sessionId, owner_secret = ownerSecret };
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/close", payload, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public async Task<RefreshSessionResponse?> RefreshSessionAsync(string baseUrl, string sessionId, string ownerSecret = "", int? expiresInSec = null, bool regeneratePass = false, CancellationToken ct = default)
    {
        var result = await RefreshSessionAsyncDetailed(baseUrl, sessionId, ownerSecret, expiresInSec, regeneratePass, ct);
        return result.Response;
    }

    /// <summary>Расширенная версия с reason'ом fail. Нужна reconnect loop'у чтобы
    /// различать "session gone on server" (fast recreate) от "network down" (retry дольше).</summary>
    public async Task<RefreshResult> RefreshSessionAsyncDetailed(string baseUrl, string sessionId, string ownerSecret = "", int? expiresInSec = null, bool regeneratePass = false, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object> { ["session_id"] = sessionId, ["owner_secret"] = ownerSecret };
        if (expiresInSec is > 0) { dict["expires_in_sec"] = expiresInSec.Value; }
        if (regeneratePass) { dict["regenerate_pass"] = true; }
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/refresh", (object)dict, ct);
            if (!response.IsSuccessStatusCode)
            {
                _onLog?.Invoke($"refresh_http_{(int)response.StatusCode}");
                // 404 "session not found", 403 "forbidden" (owner secret mismatch), 410 "expired":
                // все три означают что session больше нет на сервере (restart / expired / session wiped).
                var code = (int)response.StatusCode;
                if (code == 404 || code == 403 || code == 410)
                    return new RefreshResult { Status = RefreshStatus.SessionGone, HttpStatus = code };
                // Другие 5xx — server up но что-то внутреннее, retry.
                return new RefreshResult { Status = RefreshStatus.ServerError, HttpStatus = code };
            }

            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (json is null)
                return new RefreshResult { Status = RefreshStatus.ServerError, HttpStatus = 200 };

            var resp = new RefreshSessionResponse
            {
                SessionId = json.TryGetValue("session_id", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
                LoginCode = json.TryGetValue("login_code", out var lc) ? lc?.ToString() ?? string.Empty : string.Empty,
                PassCode = json.TryGetValue("pass_code", out var pc) ? pc?.ToString() ?? string.Empty : string.Empty,
                ExpiresInSec = TryParseInt(json, "expires_in_sec"),
                WsToken = json.TryGetValue("ws_token", out var wt) ? wt?.ToString() ?? string.Empty : string.Empty,
                TurnServers = ParseTurnServers(json)
            };
            return new RefreshResult { Status = RefreshStatus.Success, Response = resp };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _onLog?.Invoke($"api_error_{ex.GetType().Name}:{ex.Message}");
            // Network error — DNS, connection refused, timeout, SSL etc.
            // Server может быть up но интернет у user'а лёг.
            return new RefreshResult { Status = RefreshStatus.NetworkError, HttpStatus = 0 };
        }
    }

    private static int TryParseInt(Dictionary<string, object> json, string key)
    {
        if (!json.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }
        return int.TryParse(value.ToString(), out var number) ? number : 0;
    }

    private static bool TryParseBool(Dictionary<string, object> json, string key)
    {
        if (!json.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }
        return bool.TryParse(value.ToString(), out var flag) && flag;
    }

    /// <summary>
    /// Парсит "turn_servers" массив из session response'а. Возвращает null если
    /// поле отсутствует (server в legacy mode, клиент fallback'ится на static
    /// TurnUrl/Username/Password из своих settings).
    ///
    /// Поле — JsonElement (т.к. json.Deserialize'ится в Dictionary&lt;string, object&gt;
    /// через System.Text.Json, array ending up as JsonElement а не List&lt;object&gt;).
    ///
    /// Hardening (security audit 2026-04-24 — M3 finding):
    /// 1. Whitelist TURN/STUN URL schemes — rejects javascript:/file:/data:/etc
    ///    чтобы compromised signaling не мог inject'нуть attacker-controlled TURN host.
    ///    Scheme check — lowercase startsWith.
    /// 2. Size limits: urls per entry ≤8, total entries ≤8, каждая string ≤1024 chars
    ///    — защита от malicious server отправляющего 100 MB JSON → OOM.
    /// </summary>
    private static List<TurnServerConfig>? ParseTurnServers(Dictionary<string, object> json)
    {
        const int MaxEntries = 8;
        const int MaxUrlsPerEntry = 8;
        const int MaxStringLength = 1024;

        if (!json.TryGetValue("turn_servers", out var raw) || raw is null)
            return null;
        if (raw is not JsonElement elem || elem.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<TurnServerConfig>();
        int entryCount = 0;
        foreach (var item in elem.EnumerateArray())
        {
            if (entryCount++ >= MaxEntries) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var cfg = new TurnServerConfig();
            if (item.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                int urlCount = 0;
                foreach (var u in urls.EnumerateArray())
                {
                    if (urlCount++ >= MaxUrlsPerEntry) break;
                    if (u.ValueKind != JsonValueKind.String) continue;
                    var s = u.GetString();
                    if (string.IsNullOrEmpty(s) || s.Length > MaxStringLength) continue;
                    // Scheme whitelist — only TURN/STUN standard schemes.
                    // Defense against compromised signaling server sending
                    // "javascript:alert(1)" or "turn:attacker.com:3478" → отрасту на
                    // attacker relay → IP exfiltration.
                    var sl = s.ToLowerInvariant();
                    if (!sl.StartsWith("turn:") && !sl.StartsWith("turns:") &&
                        !sl.StartsWith("stun:") && !sl.StartsWith("stuns:")) continue;
                    cfg.Urls.Add(s);
                }
            }
            if (item.TryGetProperty("username", out var user) && user.ValueKind == JsonValueKind.String)
            {
                var u = user.GetString() ?? string.Empty;
                if (u.Length <= MaxStringLength) cfg.Username = u;
            }
            if (item.TryGetProperty("credential", out var cred) && cred.ValueKind == JsonValueKind.String)
            {
                var c = cred.GetString() ?? string.Empty;
                if (c.Length <= MaxStringLength) cfg.Credential = c;
            }
            if (item.TryGetProperty("expires_at_unix", out var exp) && exp.ValueKind == JsonValueKind.Number)
            {
                var expTs = exp.GetInt64();
                // Reject negative / far-past expiry (hardening — should never happen
                // from well-behaved server, но defensive если signaling compromised).
                if (expTs > 0) cfg.ExpiresAtUnix = expTs;
            }
            if (item.TryGetProperty("ttl_seconds", out var ttl) && ttl.ValueKind == JsonValueKind.Number)
            {
                var t = ttl.GetInt32();
                if (t > 0 && t <= 86400) cfg.TtlSeconds = t; // clamp [1s, 24h]
            }

            // Защита от malformed записей — skip entries без valid urls.
            if (cfg.Urls.Count > 0)
                list.Add(cfg);
        }
        return list.Count > 0 ? list : null;
    }
}

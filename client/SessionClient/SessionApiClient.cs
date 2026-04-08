using System.Net.Http.Json;

namespace SessionClient;

public sealed class SessionApiClient
{
    private readonly HttpClient _httpClient;

    public SessionApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <param name="expiresInSec">Optional TTL in seconds for unattended long-lived session (only when requestUnattended). Server caps at 7 days.</param>
    /// <param name="machineId">Persistent machine identifier. Server reuses an active session for the same machine.</param>
    public async Task<CreateSessionResponse?> CreateSessionAsync(string baseUrl, bool requestUnattended, int? expiresInSec = null, string? machineId = null, CancellationToken ct = default)
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

        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/create", payload, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (json is null)
                return null;

            return new CreateSessionResponse
            {
                SessionId = json.TryGetValue("session_id", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
                LoginCode = json.TryGetValue("login_code", out var lc) ? lc?.ToString() ?? string.Empty : string.Empty,
                PassCode = json.TryGetValue("pass_code", out var pc) ? pc?.ToString() ?? string.Empty : string.Empty,
                ExpiresInSec = TryParseInt(json, "expires_in_sec"),
                WsUrl = json.TryGetValue("ws_url", out var wsu) ? wsu?.ToString() ?? string.Empty : string.Empty,
                WsToken = json.TryGetValue("ws_token", out var token) ? token?.ToString() ?? string.Empty : string.Empty
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<JoinSessionResponse?> JoinSessionAsync(string baseUrl, string loginCode, string passCode, CancellationToken ct = default)
    {
        var payload = new
        {
            login_code = loginCode,
            pass_code = passCode
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/join", payload, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (json is null)
                return null;

            return new JoinSessionResponse
            {
                SessionId = json.TryGetValue("session_id", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
                RequireConfirm = TryParseBool(json, "require_confirm"),
                State = json.TryGetValue("state", out var state) ? state?.ToString() ?? string.Empty : string.Empty,
                WsUrl = json.TryGetValue("ws_url", out var wsu) ? wsu?.ToString() ?? string.Empty : string.Empty,
                WsToken = json.TryGetValue("ws_token", out var token) ? token?.ToString() ?? string.Empty : string.Empty
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<bool> CloseSessionAsync(string baseUrl, string sessionId, CancellationToken ct = default)
    {
        var payload = new { session_id = sessionId };
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/close", payload, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public async Task<RefreshSessionResponse?> RefreshSessionAsync(string baseUrl, string sessionId, int? expiresInSec = null, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, object> { ["session_id"] = sessionId };
        if (expiresInSec is > 0)
        {
            dict["expires_in_sec"] = expiresInSec.Value;
        }
        object payload = dict;
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/refresh", payload, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
            if (json is null)
                return null;

            return new RefreshSessionResponse
            {
                SessionId = json.TryGetValue("session_id", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
                LoginCode = json.TryGetValue("login_code", out var lc) ? lc?.ToString() ?? string.Empty : string.Empty,
                PassCode = json.TryGetValue("pass_code", out var pc) ? pc?.ToString() ?? string.Empty : string.Empty,
                ExpiresInSec = TryParseInt(json, "expires_in_sec"),
                WsToken = json.TryGetValue("ws_token", out var wt) ? wt?.ToString() ?? string.Empty : string.Empty
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
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
}

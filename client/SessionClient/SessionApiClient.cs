using System.Net.Http.Json;

namespace SessionClient;

public sealed class SessionApiClient
{
    private readonly HttpClient _httpClient;

    public SessionApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CreateSessionResponse?> CreateSessionAsync(string baseUrl, bool requestUnattended, CancellationToken ct = default)
    {
        var payload = new
        {
            request_unattended = requestUnattended
        };

        using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/create", payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
        if (json is null)
        {
            return null;
        }

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

    public async Task<JoinSessionResponse?> JoinSessionAsync(string baseUrl, string loginCode, string passCode, CancellationToken ct = default)
    {
        var payload = new
        {
            login_code = loginCode,
            pass_code = passCode
        };

        using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/join", payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
        if (json is null)
        {
            return null;
        }

        return new JoinSessionResponse
        {
            SessionId = json.TryGetValue("session_id", out var sid) ? sid?.ToString() ?? string.Empty : string.Empty,
            RequireConfirm = TryParseBool(json, "require_confirm"),
            State = json.TryGetValue("state", out var state) ? state?.ToString() ?? string.Empty : string.Empty,
            WsUrl = json.TryGetValue("ws_url", out var wsu) ? wsu?.ToString() ?? string.Empty : string.Empty,
            WsToken = json.TryGetValue("ws_token", out var token) ? token?.ToString() ?? string.Empty : string.Empty
        };
    }

    public async Task<bool> CloseSessionAsync(string baseUrl, string sessionId, CancellationToken ct = default)
    {
        var payload = new
        {
            session_id = sessionId
        };

        using var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/v1/session/close", payload, ct);
        return response.IsSuccessStatusCode;
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

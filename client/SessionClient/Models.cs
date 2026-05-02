namespace SessionClient;

public sealed class CreateSessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string LoginCode { get; set; } = string.Empty;
    public string PassCode { get; set; } = string.Empty;
    public int ExpiresInSec { get; set; }
    public string WsUrl { get; set; } = string.Empty;
    public string WsToken { get; set; } = string.Empty;
    /// <summary>F-03: ownership proof required for close/refresh. Only returned to session creator (host).</summary>
    public string OwnerSecret { get; set; } = string.Empty;
    /// <summary>NET-03: device binding secret for machine_id reuse across reboots.</summary>
    public string DeviceSecret { get; set; } = string.Empty;
    /// <summary>Rotating TURN credentials (RFC 7635). Null = server в legacy mode,
    /// клиент использует static TurnUrl/Username/Password из своих settings.</summary>
    public List<TurnServerConfig>? TurnServers { get; set; }
}

public sealed class RefreshSessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string LoginCode { get; set; } = string.Empty;
    public string PassCode { get; set; } = string.Empty;
    public int ExpiresInSec { get; set; }
    public string WsToken { get; set; } = string.Empty;
    /// <summary>Fresh rotating TURN credentials — refresh обязан выдавать новые.
    /// Клиент ДОЛЖЕН обновить peerConnection ICE config с этими creds при получении.</summary>
    public List<TurnServerConfig>? TurnServers { get; set; }
}

/// <summary>Один TURN server entry из session response'а. Матчит TurnServerConfig на сервере.</summary>
public sealed class TurnServerConfig
{
    /// <summary>TURN/STUN URLs — например ["turn:92.63.102.244:3478?transport=udp"].</summary>
    public List<string> Urls { get; set; } = new();
    /// <summary>Username для TURN auth. Формат "<exp_ts>:<session_id>".</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>HMAC-signed credential (base64). Пустой для public STUN.</summary>
    public string Credential { get; set; } = string.Empty;
    /// <summary>Unix timestamp когда credential expired. Клиент refresh'ит session до этого момента.</summary>
    public long ExpiresAtUnix { get; set; }
    /// <summary>TTL = ExpiresAtUnix - now() сервера в момент выдачи. Для client-side scheduling.</summary>
    public int TtlSeconds { get; set; }
}

/// <summary>Результат refresh — различает "session gone" (recreate) vs "network"
/// (retry дольше) vs "server 5xx" (retry но осторожно).</summary>
public enum RefreshStatus
{
    Success,
    SessionGone,   // HTTP 404/403/410 — session больше не существует на сервере
    NetworkError,  // timeout/connection refused/DNS fail — интернет у клиента лёг
    ServerError,   // HTTP 5xx — сервер up но ошибка
}

public sealed class RefreshResult
{
    public RefreshStatus Status { get; set; }
    public RefreshSessionResponse? Response { get; set; }
    public int HttpStatus { get; set; }
}

public sealed class JoinSessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public bool RequireConfirm { get; set; }
    public string State { get; set; } = string.Empty;
    public string WsUrl { get; set; } = string.Empty;
    public string WsToken { get; set; } = string.Empty;
    /// <summary>Rotating TURN credentials (RFC 7635). См. CreateSessionResponse.TurnServers.</summary>
    public List<TurnServerConfig>? TurnServers { get; set; }
}

/// <summary>Результат CreateSession — различает server side ошибки для user-facing
/// сообщений. NetworkError/ServerError trigger'ят generic error, Banned/Maintenance
/// показывают specific message + не retry'ятся автоматически.</summary>
public enum CreateSessionStatus
{
    Success,
    Banned,          // HTTP 403 "banned" — IP в blocklist'е (admin banned)
    Maintenance,     // HTTP 503 — сервер в maintenance mode
    NetworkError,    // timeout/DNS/connection refused — нет связи
    ServerError,     // HTTP 5xx — сервер вернул ошибку
    InvalidResponse, // 200 но body некорректный — protocol mismatch
}

public sealed class CreateSessionResult
{
    public CreateSessionStatus Status { get; set; }
    public CreateSessionResponse? Response { get; set; }
    public int HttpStatus { get; set; }
    /// <summary>Optional server-provided message (e.g. maintenance reason). Не гарантированно заполнен.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>Результат JoinSession — различает UI-relevant scenarios.
/// LockedUntil/Blocked показывают сколько ещё ждать; InvalidCredentials — generic
/// (сервер не различает wrong login vs wrong pass для NET-02 — не leak валидные logins).</summary>
public enum JoinSessionStatus
{
    Success,
    InvalidCredentials, // HTTP 401 — login/pass не совпали (generic, см. NET-02)
    SessionLocked,      // HTTP 429 — temp lock после N wrong attempts; RetryAfterSec > 0
    SessionBlocked,     // HTTP 423 — permanent block (host должен refresh pass). RetryAfterSec=0
    Banned,             // HTTP 403 — client IP в blocklist'е админа
    NetworkError,       // timeout/DNS
    ServerError,        // HTTP 5xx
}

public sealed class JoinSessionResult
{
    public JoinSessionStatus Status { get; set; }
    public JoinSessionResponse? Response { get; set; }
    public int HttpStatus { get; set; }
    /// <summary>Для SessionLocked — сколько секунд ждать до unlock. Server возвращает
    /// в Retry-After header + в body field retry_after_sec. 0 для Blocked/других.</summary>
    public int RetryAfterSec { get; set; }
    /// <summary>Raw error string от сервера (e.g. "banned", "session locked"). Для logging.</summary>
    public string? ErrorMessage { get; set; }
}

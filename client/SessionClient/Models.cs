namespace SessionClient;

public sealed class CreateSessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string LoginCode { get; set; } = string.Empty;
    public string PassCode { get; set; } = string.Empty;
    public int ExpiresInSec { get; set; }
    public string WsUrl { get; set; } = string.Empty;
    public string WsToken { get; set; } = string.Empty;
}

public sealed class JoinSessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public bool RequireConfirm { get; set; }
    public string State { get; set; } = string.Empty;
    public string WsUrl { get; set; } = string.Empty;
    public string WsToken { get; set; } = string.Empty;
}

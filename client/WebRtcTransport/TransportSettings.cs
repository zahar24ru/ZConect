namespace WebRtcTransport;

public sealed class TransportSettings
{
    public string StunUrl { get; set; } = string.Empty;
    public string TurnUrl { get; set; } = string.Empty;
    public string TurnUsername { get; set; } = string.Empty;
    public string TurnPassword { get; set; } = string.Empty;
    /// <summary>Additional TURN servers for failover. mrwebrtc gathers candidates from
    /// all reachable servers in parallel — if one is unreachable/rate-limited, the peer
    /// still gets relay candidates from the others. Primary TurnUrl/TurnUsername/TurnPassword
    /// are used as the first entry; this list provides fallbacks.</summary>
    public List<TurnServerEntry> TurnServers { get; set; } = new();
    public bool PreferRelay { get; set; }
    public bool PreferLanVpnNoTurn { get; set; }
}

public sealed class TurnServerEntry
{
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

namespace WebRtcTransport;

public sealed class TransportSettings
{
    public string StunUrl { get; set; } = string.Empty;
    public string TurnUrl { get; set; } = string.Empty;
    public string TurnUsername { get; set; } = string.Empty;
    public string TurnPassword { get; set; } = string.Empty;
    public bool PreferRelay { get; set; }
    public bool PreferLanVpnNoTurn { get; set; }
}

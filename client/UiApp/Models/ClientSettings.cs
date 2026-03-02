namespace UiApp.Models;

public sealed class ClientSettings
{
    public string ServerApiBaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string WebSocketUrl { get; set; } = "ws://127.0.0.1:8080/ws";
    public string StunUrl { get; set; } = "stun:127.0.0.1:3478";
    public string TurnUrl { get; set; } = "turn:127.0.0.1:3478";
    public string TurnUsername { get; set; } = "zconect";
    public string TurnPassword { get; set; } = "change_me";
    public bool PreferRelay { get; set; }
    public bool PreferLanVpnNoTurn { get; set; }
    public bool AutoIceByPriority { get; set; } = true;
    public bool RequireConfirmation { get; set; } = true;
    public bool AllowUnattended { get; set; }
    public string FfmpegPath { get; set; } = string.Empty;
    public string QualityPreset { get; set; } = "Auto";
    public string DisplayMode { get; set; } = "Current";
    public string DisplayId { get; set; } = "DISPLAY1";
    public bool DebugLogDataChannelInputEnabled { get; set; } = true;
    public bool DebugLogClipboardEnabled { get; set; } = true;
    public bool DebugLogSignalingEnabled { get; set; } = true;
    public bool DebugLogWebRtcEnabled { get; set; } = true;
}

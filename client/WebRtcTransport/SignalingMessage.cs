using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebRtcTransport;

public sealed class SignalingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

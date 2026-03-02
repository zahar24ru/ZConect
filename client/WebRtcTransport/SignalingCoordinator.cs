using System.Text.Json;

namespace WebRtcTransport;

public sealed class SignalingCoordinator
{
    private readonly WebSocketSignalingClient _signalingClient;
    private readonly IPeerConnectionAgent _peer;
    private readonly Action<string> _onLog;
    private readonly Func<Task>? _beforeCreateAnswerAsync;
    private readonly Func<string, bool>? _shouldSendLocalIceCandidate;
    private readonly Func<string, bool>? _shouldAcceptRemoteIceCandidate;
    private string _sessionId = string.Empty;
    public event Action<string, string, string, int>? IceCandidateObserved;

    public SignalingCoordinator(
        WebSocketSignalingClient signalingClient,
        IPeerConnectionAgent peer,
        Action<string> onLog,
        Func<Task>? beforeCreateAnswerAsync = null,
        Func<string, bool>? shouldSendLocalIceCandidate = null,
        Func<string, bool>? shouldAcceptRemoteIceCandidate = null)
    {
        _signalingClient = signalingClient;
        _peer = peer;
        _onLog = onLog;
        _beforeCreateAnswerAsync = beforeCreateAnswerAsync;
        _shouldSendLocalIceCandidate = shouldSendLocalIceCandidate;
        _shouldAcceptRemoteIceCandidate = shouldAcceptRemoteIceCandidate;

        _signalingClient.MessageReceived += OnMessageReceived;
        _peer.LocalIceCandidateGenerated += OnLocalIceGenerated;
    }

    public async Task StartAsCallerAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionId = sessionId;
        var sdp = await _peer.CreateOfferAsync(ct);
        await _signalingClient.SendAsync("offer", _sessionId, new { sdp }, ct);
        _onLog("offer_sdp_" + SummarizeSdp(sdp));
        _onLog("offer_sent");
    }

    public void SetSession(string sessionId)
    {
        _sessionId = sessionId;
    }

    private async void OnMessageReceived(SignalingMessage msg)
    {
        try
        {
            if (msg.Type == "offer")
            {
                var sdp = TryGetString(msg.Payload, "sdp");
                if (!string.IsNullOrWhiteSpace(sdp))
                {
                    await _peer.SetRemoteOfferAsync(sdp);
                    // Important: run hook after applying remote offer so transceivers become associated.
                    if (_beforeCreateAnswerAsync is not null)
                    {
                        await _beforeCreateAnswerAsync();
                    }
                    var answer = await _peer.CreateAnswerAsync();
                    await _signalingClient.SendAsync("answer", msg.SessionId, new { sdp = answer });
                    _onLog("answer_sdp_" + SummarizeSdp(answer));
                    _onLog("answer_sent");
                }
                return;
            }

            if (msg.Type == "answer")
            {
                var sdp = TryGetString(msg.Payload, "sdp");
                if (!string.IsNullOrWhiteSpace(sdp))
                {
                    await _peer.SetRemoteAnswerAsync(sdp);
                    _onLog("answer_received_sdp_" + SummarizeSdp(sdp));
                    _onLog("answer_received");
                }
                return;
            }

            if (msg.Type == "ice")
            {
                var candidate = TryGetString(msg.Payload, "candidate");
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    if (_shouldAcceptRemoteIceCandidate is not null && !_shouldAcceptRemoteIceCandidate(candidate))
                    {
                        _onLog("ice_remote_filtered");
                        return;
                    }
                    await _peer.AddIceCandidateAsync(candidate);
                    IceCandidateObserved?.Invoke(
                        "remote",
                        GetIceCandidateType(candidate),
                        GetIceCandidateIp(candidate),
                        GetIceCandidatePort(candidate));
                    _onLog("ice_received");
                }
            }
        }
        catch (Exception ex)
        {
            _onLog("signaling_error:" + ex.Message);
        }
    }

    private async void OnLocalIceGenerated(string candidate)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }
        if (_shouldSendLocalIceCandidate is not null && !_shouldSendLocalIceCandidate(candidate))
        {
            _onLog("ice_local_filtered");
            return;
        }
        await _signalingClient.SendAsync("ice", _sessionId, new { candidate, sdpMid = "0", sdpMLineIndex = 0 });
        IceCandidateObserved?.Invoke(
            "local",
            GetIceCandidateType(candidate),
            GetIceCandidateIp(candidate),
            GetIceCandidatePort(candidate));
        _onLog("ice_sent");
    }

    private static string TryGetString(JsonElement payload, string key)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var value))
        {
            return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string SummarizeSdp(string sdp)
    {
        // Avoid logging full SDP; only emit minimal codec/sections hints for troubleshooting.
        var hasVideo = sdp.Contains("m=video", StringComparison.OrdinalIgnoreCase);
        var hasAudio = sdp.Contains("m=audio", StringComparison.OrdinalIgnoreCase);
        var hasVp8 = sdp.Contains("VP8", StringComparison.OrdinalIgnoreCase);
        var hasH264 = sdp.Contains("H264", StringComparison.OrdinalIgnoreCase);
        var hasRtx = sdp.Contains("rtx", StringComparison.OrdinalIgnoreCase);
        var hasSctp = sdp.Contains("m=application", StringComparison.OrdinalIgnoreCase) || sdp.Contains("webrtc-datachannel", StringComparison.OrdinalIgnoreCase);
        var videoDir = FindMediaDirection(sdp, "video");
        return $"v_{hasVideo}_vd_{videoDir}_a_{hasAudio}_vp8_{hasVp8}_h264_{hasH264}_rtx_{hasRtx}_sctp_{hasSctp}";
    }

    private static string FindMediaDirection(string sdp, string media)
    {
        // Extract "a=sendrecv/recvonly/sendonly/inactive" for given m= section.
        // Not a full SDP parser; good enough for quick diagnostics.
        var lines = sdp.Split('\n');
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
            {
                inSection = line.StartsWith("m=" + media, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection)
            {
                continue;
            }
            if (line.Equals("a=sendrecv", StringComparison.OrdinalIgnoreCase)) return "sendrecv";
            if (line.Equals("a=recvonly", StringComparison.OrdinalIgnoreCase)) return "recvonly";
            if (line.Equals("a=sendonly", StringComparison.OrdinalIgnoreCase)) return "sendonly";
            if (line.Equals("a=inactive", StringComparison.OrdinalIgnoreCase)) return "inactive";
        }
        return "unknown";
    }

    private static string GetIceCandidateType(string candidate)
    {
        if (candidate.Contains(" typ relay", StringComparison.OrdinalIgnoreCase))
        {
            return "relay";
        }
        if (candidate.Contains(" typ srflx", StringComparison.OrdinalIgnoreCase))
        {
            return "srflx";
        }
        if (candidate.Contains(" typ prflx", StringComparison.OrdinalIgnoreCase))
        {
            return "prflx";
        }
        if (candidate.Contains(" typ host", StringComparison.OrdinalIgnoreCase))
        {
            return "host";
        }
        return "unknown";
    }

    private static string GetIceCandidateIp(string candidate)
    {
        try
        {
            // candidate:<foundation> <component> <transport> <priority> <ip> <port> typ <type> ...
            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6)
            {
                return parts[4];
            }
        }
        catch
        {
            // ignore
        }
        return "n/a";
    }

    private static int GetIceCandidatePort(string candidate)
    {
        try
        {
            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6 && int.TryParse(parts[5], out var port))
            {
                return port;
            }
        }
        catch
        {
            // ignore
        }
        return 0;
    }
}

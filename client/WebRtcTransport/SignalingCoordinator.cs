using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebRtcTransport;

public sealed class SignalingCoordinator
{
    private WebSocketSignalingClient _signalingClient;
    private IPeerConnectionAgent _peer;
    private readonly Action<string> _onLog;
    private readonly Func<string, Task>? _beforeCreateAnswerAsync;
    private Func<string, bool>? _shouldSendLocalIceCandidate;
    private Func<string, bool>? _shouldAcceptRemoteIceCandidate;
    private string _sessionId = string.Empty;
    private readonly List<string> _earlyLocalCandidates = new();
    private volatile bool _answerReceived;
    private int _iceSentCount;
    private int _iceReceivedCount;

    /// <summary>Fires when an ICE candidate is observed (direction, type, ip, port).</summary>
    public event Action<string, string, string, int>? IceCandidateObserved;

    /// <summary>Fires when an answer SDP is received from the remote peer.</summary>
    public event Action? AnswerReceived;

    public bool IsAnswerReceived => _answerReceived;

    public SignalingCoordinator(
        WebSocketSignalingClient signalingClient,
        IPeerConnectionAgent peer,
        Action<string> onLog,
        /// <param name="beforeCreateAnswerAsync">Вызывается после SetRemoteOffer(offerSdp). Параметр — SDP offer (чтобы решить, включать ли видео на host).</param>
        Func<string, Task>? beforeCreateAnswerAsync = null,
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

    /// <summary>Unsubscribe from signaling and peer events so a new coordinator can be attached.</summary>
    public void Detach()
    {
        _signalingClient.MessageReceived -= OnMessageReceived;
        _peer.LocalIceCandidateGenerated -= OnLocalIceGenerated;
    }

    /// <summary>Replace the underlying WS client after a reconnect. Unsubscribes from old, subscribes to new.</summary>
    public void ReplaceSignalingClient(WebSocketSignalingClient newClient)
    {
        _signalingClient.MessageReceived -= OnMessageReceived;
        _signalingClient = newClient;
        _signalingClient.MessageReceived += OnMessageReceived;
    }

    /// <summary>Replace the peer agent (e.g. after Auto ICE re-creates WebRTC). Unsubscribes from old, subscribes to new.</summary>
    public void ReplacePeerAgent(IPeerConnectionAgent newPeer)
    {
        _peer.LocalIceCandidateGenerated -= OnLocalIceGenerated;
        _peer = newPeer;
        _peer.LocalIceCandidateGenerated += OnLocalIceGenerated;
    }

    /// <summary>Update ICE candidate filters (used by Auto ICE between attempts).</summary>
    public void SetIceCandidateFilters(Func<string, bool>? shouldSendLocal, Func<string, bool>? shouldAcceptRemote)
    {
        _shouldSendLocalIceCandidate = shouldSendLocal;
        _shouldAcceptRemoteIceCandidate = shouldAcceptRemote;
    }

    public async Task StartAsCallerAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionId = sessionId;
        _answerReceived = false;
        _iceSentCount = 0;
        _iceReceivedCount = 0;
        var sdp = await _peer.CreateOfferAsync(ct);
        await _signalingClient.SendAsync("offer", _sessionId, new { sdp }, ct);
        _onLog("offer_sdp_" + SummarizeSdp(sdp));
        _onLog("offer_sent");

        // Flush any ICE candidates that were generated before SetSession/StartAsCaller
        FlushEarlyCandidates();
    }

    /// <summary>
    /// Trigger an ICE restart on the existing PeerConnection by creating a new offer
    /// with fresh ice-ufrag/ice-pwd. This forces both sides to re-gather ICE candidates
    /// while keeping media/data channels alive. Zero disruption.
    /// </summary>
    public async Task StartIceRestartAsCallerAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionId = sessionId;
        _answerReceived = false;
        var sdp = await _peer.CreateOfferAsync(ct);

        // Force ICE restart by replacing ice-ufrag and ice-pwd with new random values.
        // This tells the remote peer to discard old ICE state and gather new candidates.
        sdp = ForceIceRestart(sdp);

        await _signalingClient.SendAsync("offer", _sessionId, new { sdp }, ct);
        _onLog("ice_restart_offer_sdp_" + SummarizeSdp(sdp));
        _onLog("ice_restart_offer_sent");
        FlushEarlyCandidates();
    }

    private static string ForceIceRestart(string sdp)
    {
        var newUfrag = GenerateIceString(8);
        var newPwd = GenerateIceString(24);
        sdp = Regex.Replace(sdp, @"a=ice-ufrag:\S+", $"a=ice-ufrag:{newUfrag}");
        sdp = Regex.Replace(sdp, @"a=ice-pwd:\S+", $"a=ice-pwd:{newPwd}");
        return sdp;
    }

    private static string GenerateIceString(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+/";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        for (var i = 0; i < length; i++)
            result[i] = chars[bytes[i] % chars.Length];
        return new string(result);
    }

    public void SetSession(string sessionId)
    {
        _sessionId = sessionId;
        FlushEarlyCandidates();
    }

    private async void FlushEarlyCandidates()
    {
        List<string> buffered;
        lock (_earlyLocalCandidates)
        {
            if (_earlyLocalCandidates.Count == 0) return;
            buffered = new List<string>(_earlyLocalCandidates);
            _earlyLocalCandidates.Clear();
        }

        if (string.IsNullOrWhiteSpace(_sessionId)) return;

        foreach (var candidate in buffered)
        {
            try
            {
                if (_shouldSendLocalIceCandidate is not null && !_shouldSendLocalIceCandidate(candidate))
                    continue;

                await _signalingClient.SendAsync("ice", _sessionId, new { candidate, sdpMid = "0", sdpMLineIndex = 0 });
                IceCandidateObserved?.Invoke(
                    "local",
                    GetIceCandidateType(candidate),
                    GetIceCandidateIp(candidate),
                    GetIceCandidatePort(candidate));
                _onLog("ice_flushed_early");
            }
            catch (Exception ex)
            {
                _onLog("ice_flush_early_failed:" + ex.Message);
            }
        }
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
                    if (_beforeCreateAnswerAsync is not null)
                    {
                        await _beforeCreateAnswerAsync(sdp);
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
                    _answerReceived = true;
                    AnswerReceived?.Invoke();
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
                    var rcvCount = Interlocked.Increment(ref _iceReceivedCount);
                    if (rcvCount <= 2 || rcvCount % 10 == 0)
                        _onLog($"ice_received (#{rcvCount})");
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
        try
        {
            if (string.IsNullOrWhiteSpace(_sessionId))
            {
                lock (_earlyLocalCandidates)
                {
                    _earlyLocalCandidates.Add(candidate);
                }
                _onLog("ice_buffered_early");
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
            var count = Interlocked.Increment(ref _iceSentCount);
            if (count <= 2 || count % 10 == 0)
                _onLog($"ice_sent (#{count})");
        }
        catch (Exception ex)
        {
            // F-10 fix: catch exceptions in async void ICE handler to prevent crashes.
            _onLog($"ice_send_error: {ex.Message}");
        }
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
            if (!inSection) continue;
            if (line.Equals("a=sendrecv", StringComparison.OrdinalIgnoreCase)) return "sendrecv";
            if (line.Equals("a=recvonly", StringComparison.OrdinalIgnoreCase)) return "recvonly";
            if (line.Equals("a=sendonly", StringComparison.OrdinalIgnoreCase)) return "sendonly";
            if (line.Equals("a=inactive", StringComparison.OrdinalIgnoreCase)) return "inactive";
        }
        return "unknown";
    }

    private static string GetIceCandidateType(string candidate)
    {
        if (candidate.Contains(" typ relay", StringComparison.OrdinalIgnoreCase)) return "relay";
        if (candidate.Contains(" typ srflx", StringComparison.OrdinalIgnoreCase)) return "srflx";
        if (candidate.Contains(" typ prflx", StringComparison.OrdinalIgnoreCase)) return "prflx";
        if (candidate.Contains(" typ host", StringComparison.OrdinalIgnoreCase)) return "host";
        return "unknown";
    }

    private static string GetIceCandidateIp(string candidate)
    {
        try
        {
            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6) return parts[4];
        }
        catch { /* ignore */ }
        return "n/a";
    }

    private static int GetIceCandidatePort(string candidate)
    {
        try
        {
            var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6 && int.TryParse(parts[5], out var port)) return port;
        }
        catch { /* ignore */ }
        return 0;
    }
}

namespace WebRtcTransport;

public interface IPeerConnectionAgent
{
    event Action<string>? LocalIceCandidateGenerated;
    event Action<RemoteVideoFrame>? RemoteVideoFrameReceived;
    /// <summary>Fired when ICE state transitions to Failed — P2P connection is dead.</summary>
    event Action? IceStateFailed;
    /// <summary>Fired when ICE state transitions to Disconnected — may recover on its own
    /// (e.g. brief WiFi hiccup) or escalate to Failed. UI shows transient "reconnecting" banner.</summary>
    event Action? IceStateDisconnected;
    /// <summary>Fired when ICE state returns to Connected/Completed after a Disconnected period.
    /// UI clears the "reconnecting" banner.</summary>
    event Action? IceStateReconnected;
    /// <param name="includeVideoTransceiver">Если false, видео не добавляется в SDP (режим «только передача файлов»).</param>
    Task InitializeAsync(CancellationToken ct = default, bool includeVideoTransceiver = true);
    Task ConfigureLocalVideoAsync(LocalVideoOptions? options, CancellationToken ct = default);
    Task<string> CreateOfferAsync(CancellationToken ct = default);
    Task<string> CreateAnswerAsync(CancellationToken ct = default);
    Task SetRemoteOfferAsync(string sdp, CancellationToken ct = default);
    Task SetRemoteAnswerAsync(string sdp, CancellationToken ct = default);
    Task AddIceCandidateAsync(string candidate, CancellationToken ct = default);
}

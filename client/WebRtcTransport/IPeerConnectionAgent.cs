namespace WebRtcTransport;

public interface IPeerConnectionAgent
{
    event Action<string>? LocalIceCandidateGenerated;
    event Action<RemoteVideoFrame>? RemoteVideoFrameReceived;
    /// <param name="includeVideoTransceiver">Если false, видео не добавляется в SDP (режим «только передача файлов»).</param>
    Task InitializeAsync(CancellationToken ct = default, bool includeVideoTransceiver = true);
    Task ConfigureLocalVideoAsync(LocalVideoOptions? options, CancellationToken ct = default);
    Task<string> CreateOfferAsync(CancellationToken ct = default);
    Task<string> CreateAnswerAsync(CancellationToken ct = default);
    Task SetRemoteOfferAsync(string sdp, CancellationToken ct = default);
    Task SetRemoteAnswerAsync(string sdp, CancellationToken ct = default);
    Task AddIceCandidateAsync(string candidate, CancellationToken ct = default);
}

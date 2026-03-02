namespace WebRtcTransport;

public interface IPeerConnectionAgent
{
    event Action<string>? LocalIceCandidateGenerated;
    event Action<RemoteVideoFrame>? RemoteVideoFrameReceived;
    Task InitializeAsync(CancellationToken ct = default);
    Task ConfigureLocalVideoAsync(LocalVideoOptions? options, CancellationToken ct = default);
    Task<string> CreateOfferAsync(CancellationToken ct = default);
    Task<string> CreateAnswerAsync(CancellationToken ct = default);
    Task SetRemoteOfferAsync(string sdp, CancellationToken ct = default);
    Task SetRemoteAnswerAsync(string sdp, CancellationToken ct = default);
    Task AddIceCandidateAsync(string candidate, CancellationToken ct = default);
}

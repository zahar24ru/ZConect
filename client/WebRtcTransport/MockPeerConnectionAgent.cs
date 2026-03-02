namespace WebRtcTransport;

// Временный агент-заглушка для сквозной проверки signaling.
// На следующем шаге будет заменен реальным WebRTC engine.
public sealed class MockPeerConnectionAgent : IPeerConnectionAgent
{
    public event Action<string>? LocalIceCandidateGenerated;
    public event Action<RemoteVideoFrame>? RemoteVideoFrameReceived
    {
        add { }
        remove { }
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ConfigureLocalVideoAsync(LocalVideoOptions? options, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> CreateOfferAsync(CancellationToken ct = default)
    {
        LocalIceCandidateGenerated?.Invoke("candidate:mock-offer");
        return Task.FromResult("v=0\r\ns=mock-offer\r\n");
    }

    public Task<string> CreateAnswerAsync(CancellationToken ct = default)
    {
        LocalIceCandidateGenerated?.Invoke("candidate:mock-answer");
        return Task.FromResult("v=0\r\ns=mock-answer\r\n");
    }

    public Task SetRemoteOfferAsync(string sdp, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetRemoteAnswerAsync(string sdp, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddIceCandidateAsync(string candidate, CancellationToken ct = default) => Task.CompletedTask;
}

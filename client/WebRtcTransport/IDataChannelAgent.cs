namespace WebRtcTransport;

public interface IDataChannelAgent
{
    event Action<DataChannelKind, string>? MessageReceived;
    Task SendTextAsync(DataChannelKind kind, string text, CancellationToken ct = default);
}

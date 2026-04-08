namespace WebRtcTransport;

public interface IDataChannelAgent
{
    event Action<DataChannelKind, string>? MessageReceived;
    event Action<DataChannelKind, byte[]>? BinaryMessageReceived;
    /// <summary>Fires when a data channel transitions to Open state.</summary>
    event Action<DataChannelKind>? ChannelOpened;
    Task SendTextAsync(DataChannelKind kind, string text, CancellationToken ct = default);
    Task SendBinaryAsync(DataChannelKind kind, byte[] data, CancellationToken ct = default);
}

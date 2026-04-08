namespace WebRtcTransport;

public sealed class MockDataChannelAgent : IDataChannelAgent
{
    public event Action<DataChannelKind, string>? MessageReceived;
    public event Action<DataChannelKind, byte[]>? BinaryMessageReceived;
    public event Action<DataChannelKind>? ChannelOpened
    { add { } remove { } }

    public Task SendTextAsync(DataChannelKind kind, string text, CancellationToken ct = default)
    {
        // Временный loopback-режим: эмулируем доставку локально.
        MessageReceived?.Invoke(kind, text);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(DataChannelKind kind, byte[] data, CancellationToken ct = default)
    {
        BinaryMessageReceived?.Invoke(kind, data);
        return Task.CompletedTask;
    }
}

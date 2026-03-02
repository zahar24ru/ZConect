namespace WebRtcTransport;

public sealed class MockDataChannelAgent : IDataChannelAgent
{
    public event Action<DataChannelKind, string>? MessageReceived;

    public Task SendTextAsync(DataChannelKind kind, string text, CancellationToken ct = default)
    {
        // Временный loopback-режим: эмулируем доставку локально.
        MessageReceived?.Invoke(kind, text);
        return Task.CompletedTask;
    }
}

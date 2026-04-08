using System.Text;
using Microsoft.MixedReality.WebRTC;

namespace WebRtcTransport;

public sealed class MixedRealityDataChannelAgent : IDataChannelAgent
{
    // Binary file chunk marker — first byte distinguishes binary from JSON text.
    private const byte BinaryChunkMarker = 0x01;

    private readonly Dictionary<DataChannelKind, DataChannel> _channels = new();

    public event Action<DataChannelKind, string>? MessageReceived;
    public event Action<DataChannelKind, byte[]>? BinaryMessageReceived;
    public event Action<DataChannelKind>? ChannelOpened;

    public void AttachChannel(DataChannel channel)
    {
        var kind = MapKind(channel.Label);
        if (kind is null)
        {
            return;
        }

        if (_channels.TryGetValue(kind.Value, out var existing) && ReferenceEquals(existing, channel))
        {
            // Avoid double subscription if the same channel is delivered twice.
            return;
        }

        _channels[kind.Value] = channel;
        channel.MessageReceived += bytes =>
        {
            // Binary chunk: first byte is 0x01 marker.
            if (bytes.Length > 0 && bytes[0] == BinaryChunkMarker)
            {
                BinaryMessageReceived?.Invoke(kind.Value, bytes);
                return;
            }

            var text = Encoding.UTF8.GetString(bytes);
            MessageReceived?.Invoke(kind.Value, text);
        };

        // Fire ChannelOpened when the channel transitions to Open.
        if (channel.State == DataChannel.ChannelState.Open)
        {
            ChannelOpened?.Invoke(kind.Value);
        }
        else
        {
            var k = kind.Value;
            channel.StateChanged += () =>
            {
                if (channel.State == DataChannel.ChannelState.Open)
                    ChannelOpened?.Invoke(k);
            };
        }
    }

    public Task SendTextAsync(DataChannelKind kind, string text, CancellationToken ct = default)
    {
        var ch = GetOpenChannel(kind);
        ch.SendMessage(Encoding.UTF8.GetBytes(text));
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(DataChannelKind kind, byte[] data, CancellationToken ct = default)
    {
        var ch = GetOpenChannel(kind);
        ch.SendMessage(data);
        return Task.CompletedTask;
    }

    private DataChannel GetOpenChannel(DataChannelKind kind)
    {
        if (!_channels.TryGetValue(kind, out var ch))
            throw new InvalidOperationException($"Data channel {kind} is not ready.");
        if (ch.State != DataChannel.ChannelState.Open)
            throw new InvalidOperationException($"Data channel {kind} is not open (state: {ch.State}).");
        return ch;
    }

    private static DataChannelKind? MapKind(string label)
    {
        return label switch
        {
            "dc-control" => DataChannelKind.Control,
            "dc-input" => DataChannelKind.Input,
            "dc-clipboard" => DataChannelKind.Clipboard,
            "dc-file" => DataChannelKind.File,
            _ => null
        };
    }
}

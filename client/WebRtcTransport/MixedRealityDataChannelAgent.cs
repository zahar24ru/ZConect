using System.Text;
using Microsoft.MixedReality.WebRTC;

namespace WebRtcTransport;

public sealed class MixedRealityDataChannelAgent : IDataChannelAgent
{
    private readonly Dictionary<DataChannelKind, DataChannel> _channels = new();

    public event Action<DataChannelKind, string>? MessageReceived;

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
            var text = Encoding.UTF8.GetString(bytes);
            MessageReceived?.Invoke(kind.Value, text);
        };
    }

    public Task SendTextAsync(DataChannelKind kind, string text, CancellationToken ct = default)
    {
        if (!_channels.TryGetValue(kind, out var ch))
        {
            throw new InvalidOperationException($"Data channel {kind} is not ready.");
        }

        ch.SendMessage(Encoding.UTF8.GetBytes(text));
        return Task.CompletedTask;
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

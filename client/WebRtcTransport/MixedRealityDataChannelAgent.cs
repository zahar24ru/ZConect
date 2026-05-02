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

    public async Task SendTextAsync(DataChannelKind kind, string text, CancellationToken ct = default)
    {
        // Wait for channel to transition Connecting→Open (up to ~3 сек).
        // SCTP channels открываются не одновременно — Control обычно готов
        // на несколько сотен мс раньше File. Если viewer шлёт file_request
        // сразу после dc_file_open, host'овский file_error-ответ может попасть
        // в окно когда File-channel ещё Connecting → InvalidOperationException →
        // UnobservedTaskException (observed 2026-04-19 crashes.log).
        var ch = await WaitForOpenChannelAsync(kind, ct).ConfigureAwait(false);
        ch.SendMessage(Encoding.UTF8.GetBytes(text));
    }

    private async Task<DataChannel> WaitForOpenChannelAsync(DataChannelKind kind, CancellationToken ct)
    {
        if (!_channels.TryGetValue(kind, out var ch))
            throw new InvalidOperationException($"Data channel {kind} is not ready.");
        if (ch.State == DataChannel.ChannelState.Open) return ch;

        // Short exponential backoff up to 3 сек total.
        var delays = new[] { 50, 100, 200, 300, 500, 700, 1200 };
        foreach (var d in delays)
        {
            await Task.Delay(d, ct).ConfigureAwait(false);
            if (ch.State == DataChannel.ChannelState.Open) return ch;
        }
        throw new InvalidOperationException($"Data channel {kind} is not open (state: {ch.State}) after 3s wait.");
    }

    public async Task SendBinaryAsync(DataChannelKind kind, byte[] data, CancellationToken ct = default)
    {
        var ch = GetOpenChannel(kind);
        // Retry with backpressure when SCTP send buffer is full.
        // DataChannel.SendMessage throws bare System.Exception on buffer overflow.
        for (int retry = 0; retry < 50; retry++)
        {
            try
            {
                ch.SendMessage(data);
                return;
            }
            catch (Exception) when (retry < 49)
            {
                // SCTP buffer full — wait for it to drain.
                var delay = Math.Min(20 * (retry + 1), 500);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        ch.SendMessage(data); // last attempt — let it throw
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

namespace WebRtcTransport;

public sealed class RemoteVideoFrame
{
    public required byte[] Buffer { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
}

namespace WebRtcTransport;

public sealed class LocalVideoOptions
{
    public int CaptureX { get; init; }
    public int CaptureY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; } = 30;
}

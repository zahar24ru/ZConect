namespace WebRtcTransport;

public sealed class LocalVideoOptions
{
    public int CaptureX { get; init; }
    public int CaptureY { get; init; }
    /// <summary>Source (native screen) width to capture.</summary>
    public int SourceWidth { get; init; }
    /// <summary>Source (native screen) height to capture.</summary>
    public int SourceHeight { get; init; }
    /// <summary>Output width fed to WebRTC encoder. If smaller than SourceWidth, the frame is scaled down.</summary>
    public int Width { get; init; }
    /// <summary>Output height fed to WebRTC encoder. If smaller than SourceHeight, the frame is scaled down.</summary>
    public int Height { get; init; }
    public int Fps { get; init; } = 30;

    /// <summary>True when output size differs from source size and scaling is needed.</summary>
    public bool NeedsScaling => Width != SourceWidth || Height != SourceHeight;

    /// <summary>Force GDI capture (e.g. multi-monitor "All" mode where DXGI can only capture one output).</summary>
    public bool ForceGdi { get; init; }
}

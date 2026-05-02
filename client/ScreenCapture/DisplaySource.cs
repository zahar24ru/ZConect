namespace ScreenCapture;

public sealed class DisplaySource
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }
}

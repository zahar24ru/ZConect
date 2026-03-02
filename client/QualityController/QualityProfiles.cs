namespace QualityController;

public sealed class QualityProfile
{
    public string Name { get; init; } = "Auto";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
    public int BitrateKbps { get; init; }
}

public static class QualityProfiles
{
    public static readonly QualityProfile Auto = new()
    {
        Name = "Auto",
        Width = 1280,
        Height = 720,
        Fps = 30,
        BitrateKbps = 2200
    };

    public static readonly QualityProfile Low = new()
    {
        Name = "Low",
        Width = 854,
        Height = 480,
        Fps = 15,
        BitrateKbps = 900
    };

    public static readonly QualityProfile Medium = new()
    {
        Name = "Medium",
        Width = 1280,
        Height = 720,
        Fps = 30,
        BitrateKbps = 2200
    };

    public static readonly QualityProfile High = new()
    {
        Name = "High",
        Width = 1920,
        Height = 1080,
        Fps = 30,
        BitrateKbps = 4200
    };

    public static QualityProfile Resolve(string preset)
    {
        return preset.ToLowerInvariant() switch
        {
            "low" => Low,
            "medium" => Medium,
            "high" => High,
            _ => Auto
        };
    }
}

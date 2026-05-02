using QualityController;
using Xunit;

namespace ZConect.Tests;

/// <summary>Tests for auto quality adaptation logic (bitrate/frame drop monitoring).</summary>
public sealed class AutoQualityAdapterTests
{
    [Fact]
    public void Default_preset_is_Medium()
    {
        var adapter = new AutoQualityAdapter();
        Assert.Equal("Medium", adapter.CurrentPreset);
    }

    [Fact]
    public void Custom_initial_preset()
    {
        var adapter = new AutoQualityAdapter(initialPreset: "Low");
        Assert.Equal("Low", adapter.CurrentPreset);
    }

    [Fact]
    public void First_update_returns_null_baseline()
    {
        var adapter = new AutoQualityAdapter();
        var result = adapter.Update(1_000_000, 100_000, 30, 30);
        Assert.Null(result); // first sample = baseline
    }

    [Fact]
    public void Good_conditions_no_immediate_change()
    {
        var adapter = new AutoQualityAdapter();
        adapter.Update(0, 0, 0, 0); // baseline

        // Good conditions: high bitrate, no frame drops
        var result = adapter.Update(1_500_000, 500_000, 30, 30);
        Assert.Null(result); // still in cooldown
    }

    [Fact]
    public void Degradation_after_streak()
    {
        var adapter = new AutoQualityAdapter(initialPreset: "High");
        adapter.Update(0, 0, 0, 0); // baseline

        // Simulate bad conditions: very low bitrate + high frame drops
        string? result = null;
        for (int i = 0; i < 20; i++)
        {
            var ts = (long)(i + 1) * 1_500_000; // 1.5s apart
            // Low bitrate (100 bytes in 1.5s = ~67 B/s) and many dropped frames
            result = adapter.Update(ts, 100 * (i + 1), (i + 1) * 5, (i + 1) * 30);
            if (result is not null) break;
        }

        Assert.NotNull(result);
        Assert.NotEqual("High", result); // should have degraded
    }

    [Fact]
    public void Reset_clears_state()
    {
        var adapter = new AutoQualityAdapter(initialPreset: "High");
        adapter.Update(0, 0, 0, 0);
        adapter.Update(1_500_000, 50000, 30, 30);

        adapter.Reset("Low");
        Assert.Equal("Low", adapter.CurrentPreset);

        // After reset, first update is baseline again
        var result = adapter.Update(1_000_000, 100_000, 30, 30);
        Assert.Null(result);
    }

    [Fact]
    public void Reset_without_preset_keeps_current()
    {
        var adapter = new AutoQualityAdapter(initialPreset: "High");
        adapter.Reset();
        Assert.Equal("High", adapter.CurrentPreset);
    }

    [Fact]
    public void Cannot_degrade_below_ExtraLow()
    {
        var adapter = new AutoQualityAdapter(initialPreset: "Extra Low");
        adapter.Update(0, 0, 0, 0);

        // Many bad samples
        for (int i = 0; i < 30; i++)
            adapter.Update((long)(i + 1) * 1_500_000, 10 * (i + 1), (i + 1), (i + 1) * 30);

        Assert.Equal("Extra Low", adapter.CurrentPreset);
    }

    [Fact]
    public void Invalid_initial_preset_defaults_to_Medium()
    {
        var adapter = new AutoQualityAdapter(initialPreset: "NonExistent");
        Assert.Equal("Medium", adapter.CurrentPreset);
    }

    [Theory]
    [InlineData("Extra Low", 0)]
    [InlineData("Low", 1)]
    [InlineData("Medium", 2)]
    [InlineData("High", 3)]
    public void All_presets_accepted(string preset, int _)
    {
        var adapter = new AutoQualityAdapter(initialPreset: preset);
        Assert.Equal(preset, adapter.CurrentPreset);
    }
}

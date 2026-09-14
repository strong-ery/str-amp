using Stramp.Core.Playback;

namespace Stramp.Core.Tests;

public class EqualizerPresetTests
{
    private static readonly float[] PlayerBands =
        [63, 110, 250, 370, 650, 1200, 2130, 4550, 6850, 16000];

    [Fact]
    public void GainsFor_ReturnsOneGainPerBand()
    {
        foreach (var preset in EqualizerPresets.All)
            Assert.Equal(PlayerBands.Length, preset.GainsFor(PlayerBands).Length);
    }

    [Fact]
    public void GainsFor_ReproducesTheCurveExactlyAtItsOwnFrequencies()
    {
        foreach (var preset in EqualizerPresets.All)
        {
            var own = preset.Points.Select(p => p.Hz).ToArray();
            var gains = preset.GainsFor(own);

            for (var i = 0; i < own.Length; i++)
                Assert.Equal(preset.Points[i].GainDb, gains[i], 6);
        }
    }

    [Fact]
    public void GainsFor_InterpolatesHalfwayInLogFrequency()
    {
        // 100 Hz and 400 Hz are two octaves apart, so 200 Hz is the midpoint on a log axis.
        var preset = new EqualizerPreset("test", [new(100, 0), new(400, 8)]);

        var gains = preset.GainsFor([100, 200, 400]);

        Assert.Equal(0, gains[0], 6);
        Assert.Equal(4, gains[1], 6);
        Assert.Equal(8, gains[2], 6);
    }

    [Fact]
    public void GainsFor_HoldsTheEndValuesOutsideTheCurve()
    {
        var preset = new EqualizerPreset("test", [new(100, -3), new(400, 5)]);

        var gains = preset.GainsFor([20, 100, 400, 20000]);

        Assert.Equal(-3, gains[0], 6);
        Assert.Equal(-3, gains[1], 6);
        Assert.Equal(5, gains[2], 6);
        Assert.Equal(5, gains[3], 6);
    }

    [Fact]
    public void GainsFor_HandlesAnEmptyOrSinglePointCurve()
    {
        Assert.Equal([0, 0], new EqualizerPreset("empty", []).GainsFor([100, 200]));
        Assert.Equal([7, 7], new EqualizerPreset("one", [new(100, 7)]).GainsFor([50, 5000]));
    }

    [Fact]
    public void BuiltInPresets_AreWellFormed()
    {
        Assert.NotEmpty(EqualizerPresets.All);
        Assert.Equal(
            EqualizerPresets.All.Select(p => p.Name).Distinct().Count(),
            EqualizerPresets.All.Count);

        foreach (var preset in EqualizerPresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.NotEmpty(preset.Points);

            // Ascending frequencies, or the interpolation walk would skip points.
            var frequencies = preset.Points.Select(p => p.Hz).ToArray();
            Assert.Equal(frequencies.OrderBy(f => f), frequencies);
            Assert.All(frequencies, f => Assert.True(f > 0));

            // These are gentle tone curves; anything outside the slider range means a bad import.
            Assert.All(preset.GainsFor(PlayerBands), g => Assert.InRange(g, -20, 20));
        }
    }
}

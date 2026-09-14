using Stramp.Core.Playback;

namespace Stramp.Core.Tests;

public class EqualizerBandLayoutTests
{
    private static readonly double[] Bands =
        [63, 110, 250, 370, 650, 1200, 2130, 4550, 6850, 16000];

    [Fact]
    public void Constrain_LeavesAValueBetweenItsNeighboursAlone()
    {
        Assert.Equal(300, EqualizerBandLayout.Constrain(Bands, 3, 300));
        Assert.Equal(500, EqualizerBandLayout.Constrain(Bands, 3, 500));
    }

    [Fact]
    public void Constrain_StopsABandOvertakingTheOneAboveIt()
    {
        // Band 3 sits at 370 with 650 above it; pushing it to 5000 must stop just below 650.
        var result = EqualizerBandLayout.Constrain(Bands, 3, 5000);

        Assert.True(result < 650, $"expected below 650 Hz, got {result}");
        Assert.Equal(650 / EqualizerBandLayout.MinSpacingRatio, result, 6);
    }

    [Fact]
    public void Constrain_StopsABandFallingBelowTheOneUnderIt()
    {
        var result = EqualizerBandLayout.Constrain(Bands, 3, 30);

        Assert.True(result > 250, $"expected above 250 Hz, got {result}");
        Assert.Equal(250 * EqualizerBandLayout.MinSpacingRatio, result, 6);
    }

    [Fact]
    public void Constrain_HoldsTheOuterBandsToTheOverallRange()
    {
        Assert.Equal(EqualizerBandLayout.MinFrequencyHz, EqualizerBandLayout.Constrain(Bands, 0, 1));
        Assert.Equal(EqualizerBandLayout.MaxFrequencyHz, EqualizerBandLayout.Constrain(Bands, 9, 999999));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-500)]
    public void Constrain_RejectsNonsenseWithoutThrowing(double requested)
    {
        var result = EqualizerBandLayout.Constrain(Bands, 0, requested);

        Assert.True(double.IsFinite(result));
        Assert.InRange(result, EqualizerBandLayout.MinFrequencyHz, EqualizerBandLayout.MaxFrequencyHz);
    }

    [Fact]
    public void Constrain_KeepsTheWholeSetAscendingWhateverIsTyped()
    {
        var bands = (double[])Bands.Clone();
        var rng = new Random(11);

        // Hammer random bands with random values, as a user dragging through the fields would.
        for (var edit = 0; edit < 2000; edit++)
        {
            var index = rng.Next(bands.Length);
            bands[index] = EqualizerBandLayout.Constrain(bands, index, rng.NextDouble() * 25000 - 2500);

            for (var i = 1; i < bands.Length; i++)
                Assert.True(bands[i] > bands[i - 1],
                    $"edit {edit} left band {i} ({bands[i]}) at or below band {i - 1} ({bands[i - 1]})");
        }
    }

    [Fact]
    public void Constrain_PinsToTheFloorWhenNeighboursLeaveNoRoom()
    {
        // Neighbours closer together than the minimum spacing: there is no legal gap to land in.
        double[] crowded = [1000, 1000.5, 1001];

        var result = EqualizerBandLayout.Constrain(crowded, 1, 1000.5);

        Assert.True(double.IsFinite(result));
        Assert.Equal(1000 * EqualizerBandLayout.MinSpacingRatio, result, 6);
    }

    [Fact]
    public void Constrain_HandlesASingleBand()
    {
        double[] one = [1000];

        Assert.Equal(5000, EqualizerBandLayout.Constrain(one, 0, 5000));
        Assert.Equal(EqualizerBandLayout.MaxFrequencyHz, EqualizerBandLayout.Constrain(one, 0, 90000));
    }

    [Fact]
    public void Constrain_RejectsAnIndexOutsideTheBands()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EqualizerBandLayout.Constrain(Bands, -1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => EqualizerBandLayout.Constrain(Bands, Bands.Length, 100));
    }

    [Fact]
    public void BuiltInPresets_AllSatisfyTheLayoutRules()
    {
        foreach (var preset in EqualizerPresets.All)
        {
            var frequencies = preset.Points.Select(p => (double)p.Hz).ToArray();

            for (var i = 1; i < frequencies.Length; i++)
                Assert.True(frequencies[i] >= frequencies[i - 1] * EqualizerBandLayout.MinSpacingRatio,
                    $"{preset.Name}: bands {i - 1} and {i} are closer than the minimum spacing");

            Assert.All(frequencies, f =>
                Assert.InRange(f, EqualizerBandLayout.MinFrequencyHz, EqualizerBandLayout.MaxFrequencyHz));
        }
    }
}

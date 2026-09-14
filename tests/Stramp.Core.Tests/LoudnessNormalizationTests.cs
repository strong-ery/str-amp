using Stramp.Core.Playback;

namespace Stramp.Core.Tests;

public class LoudnessNormalizationTests
{
    [Fact]
    public void CalculateGainDb_ReachesTargetWhenPeakHasHeadroom()
    {
        var gain = LoudnessNormalization.CalculateGainDb(new TrackLoudness(-20, -8));

        Assert.Equal(6, gain);
    }

    [Fact]
    public void CalculateGainDb_LimitsBoostToTruePeakHeadroom()
    {
        var gain = LoudnessNormalization.CalculateGainDb(new TrackLoudness(-20, -3));

        Assert.Equal(2, gain);
    }

    [Fact]
    public void CalculateGainDb_AttenuatesLoudTracksWithoutCompression()
    {
        var gain = LoudnessNormalization.CalculateGainDb(new TrackLoudness(-8, -0.5));

        Assert.Equal(-6, gain);
    }

    [Fact]
    public void CalculateGainDb_InvalidMeasurementUsesUnityGain()
    {
        var gain = LoudnessNormalization.CalculateGainDb(
            new TrackLoudness(double.NegativeInfinity, -1));

        Assert.Equal(0, gain);
    }

    [Theory]
    [InlineData(AudioNormalizationLevel.Quiet, -5)]
    [InlineData(AudioNormalizationLevel.Normal, 4)]
    [InlineData(AudioNormalizationLevel.Loud, 9)]
    public void CalculateGainDb_UsesSelectedLoudnessTarget(
        AudioNormalizationLevel level, double expectedGain)
    {
        var gain = LoudnessNormalization.CalculateGainDb(new TrackLoudness(-18, -12), level);

        Assert.Equal(expectedGain, gain);
    }
}

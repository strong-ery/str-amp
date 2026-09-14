using Stramp.Core.Dsp;

namespace Stramp.Core.Tests;

public class BeatDetectorTests
{
    [Fact]
    public void Update_SpikeAboveQuietBaseline_FiresHit()
    {
        var detector = new BeatDetector(sensitivity: 1.5, refractoryFrames: 6);

        for (var i = 0; i < 20; i++)
            detector.Update(0.01f);

        Assert.True(detector.Update(1.0f).IsHit);
    }

    [Fact]
    public void Update_WithinRefractoryPeriod_DoesNotRetrigger()
    {
        var detector = new BeatDetector(sensitivity: 1.5, refractoryFrames: 6);
        for (var i = 0; i < 20; i++)
            detector.Update(0.01f);

        Assert.True(detector.Update(1.0f).IsHit);
        Assert.False(detector.Update(1.0f).IsHit);
    }

    [Fact]
    public void Update_SteadyEnergy_NeverFires()
    {
        var detector = new BeatDetector();

        var anyHit = false;
        for (var i = 0; i < 50; i++)
            anyHit |= detector.Update(0.2f).IsHit;

        Assert.False(anyHit);
    }

    [Fact]
    public void Update_BiggerOvershoot_ReportsHigherStrength()
    {
        var soft = new BeatDetector(sensitivity: 1.5, refractoryFrames: 6);
        var hard = new BeatDetector(sensitivity: 1.5, refractoryFrames: 6);
        for (var i = 0; i < 20; i++)
        {
            soft.Update(0.1f);
            hard.Update(0.1f);
        }

        var softHit = soft.Update(0.18f);
        var hardHit = hard.Update(2.0f);

        Assert.True(softHit.IsHit);
        Assert.True(hardHit.IsHit);
        Assert.True(hardHit.Strength > softHit.Strength);
        Assert.InRange(hardHit.Strength, 0f, 1f);
    }
}

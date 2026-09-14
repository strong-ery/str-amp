using Stramp.Core.Dsp;

namespace Stramp.Core.Tests;

public class SpectrumAnalyzerTests
{
    private const int SampleRate = 44100;
    private const int WindowSize = 2048;

    private static float[] MakeSine(float hz, int n, float amplitude = 1f)
    {
        var samples = new float[n];
        for (var i = 0; i < n; i++)
            samples[i] = amplitude * MathF.Sin(2 * MathF.PI * hz * i / SampleRate);
        return samples;
    }

    [Fact]
    public void Analyze_BassTone_HasHigherLowBandThanHighBand()
    {
        var samples = MakeSine(80f, WindowSize); // inside the low/kick band

        var frame = SpectrumAnalyzer.Analyze(samples, SampleRate, 32);

        Assert.True(frame.LowBandEnergy > frame.HighBandEnergy);
    }

    [Fact]
    public void Analyze_TrebleTone_HasHigherHighBandThanLowBand()
    {
        var samples = MakeSine(6000f, WindowSize); // inside the hi-hat band

        var frame = SpectrumAnalyzer.Analyze(samples, SampleRate, 32);

        Assert.True(frame.HighBandEnergy > frame.LowBandEnergy);
    }

    [Fact]
    public void Analyze_DisplayBins_AreAllWithinZeroToOne()
    {
        var samples = MakeSine(440f, WindowSize);

        var frame = SpectrumAnalyzer.Analyze(samples, SampleRate, 48);

        Assert.Equal(48, frame.DisplayBins.Length);
        Assert.All(frame.DisplayBins, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void Analyze_QuietAndLoudTones_DoNotBothSaturate()
    {
        // Regression: the display scale used to compress so hard that everything clamped to 1.0,
        // which made the visualizer a solid block pinned to the top of its bounds.
        var loud = SpectrumAnalyzer.Analyze(MakeSine(1000f, WindowSize), SampleRate, 48);
        var quiet = SpectrumAnalyzer.Analyze(MakeSine(1000f, WindowSize, 0.02f), SampleRate, 48);

        Assert.True(quiet.DisplayBins.Max() < loud.DisplayBins.Max());
        Assert.True(loud.DisplayBins.Max() > 0.3f, "a full-scale tone should still read strongly");
    }

    [Fact]
    public void Analyze_Silence_ProducesZeroEnergy()
    {
        var samples = new float[WindowSize];

        var frame = SpectrumAnalyzer.Analyze(samples, SampleRate, 32);

        Assert.Equal(0f, frame.LowBandEnergy);
        Assert.Equal(0f, frame.HighBandEnergy);
        Assert.All(frame.DisplayBins, v => Assert.Equal(0f, v));
    }
}

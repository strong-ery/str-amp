namespace Stramp.Core.Dsp;

/// <summary>
/// Turns a raw mono PCM window into a display-ready spectrum plus low/high band energies.
/// Stateless: callers own the PCM buffer and pick which window (e.g. "the samples up to the
/// current playback position") to analyze each tick.
/// </summary>
public static class SpectrumAnalyzer
{
    private const float LowBandMinHz = 40f;
    private const float LowBandMaxHz = 150f;
    private const float HighBandMinHz = 4000f;
    private const float HighBandMaxHz = 10000f;
    private const float DisplayMinHz = 30f;
    private const float DisplayMaxHz = 14000f;

    /// <summary>`window.Length` must be a power of two.</summary>
    public static SpectrumFrame Analyze(ReadOnlySpan<float> window, int sampleRate, int displayBinCount)
    {
        var n = window.Length;
        var real = new float[n];
        var imag = new float[n];

        for (var i = 0; i < n; i++)
        {
            // Hann window to reduce spectral leakage.
            var w = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (n - 1)));
            real[i] = window[i] * w;
        }

        Fft.Forward(real, imag);

        var usable = n / 2;
        var magnitude = new float[usable];
        for (var i = 0; i < usable; i++)
            magnitude[i] = MathF.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

        var hzPerBin = (float)sampleRate / n;
        var displayBins = new float[displayBinCount];

        for (var b = 0; b < displayBinCount; b++)
        {
            var f0 = BinFrequency((float)b / displayBinCount);
            var f1 = BinFrequency((float)(b + 1) / displayBinCount);
            var mean = AverageMagnitudeInRange(magnitude, hzPerBin, f0, f1);
            displayBins[b] = NormalizeForDisplay(mean, n, (f0 + f1) * 0.5f);
        }

        var lowEnergy = AverageMagnitudeInRange(magnitude, hzPerBin, LowBandMinHz, LowBandMaxHz);
        var highEnergy = AverageMagnitudeInRange(magnitude, hzPerBin, HighBandMinHz, HighBandMaxHz);

        return new SpectrumFrame
        {
            DisplayBins = displayBins,
            LowBandEnergy = lowEnergy,
            HighBandEnergy = highEnergy,
        };
    }

    private static float AverageMagnitudeInRange(float[] magnitude, float hzPerBin, float loHz, float hiHz)
    {
        var i0 = Math.Clamp((int)(loHz / hzPerBin), 0, magnitude.Length - 1);
        var i1 = Math.Clamp((int)(hiHz / hzPerBin), i0 + 1, magnitude.Length);

        float sum = 0;
        for (var i = i0; i < i1; i++)
            sum += magnitude[i];

        return sum / (i1 - i0);
    }

    private const float FloorDb = -72f;
    private const float SpacingExponent = 1.7f;

    /// <summary>
    /// Maps a 0-1 position across the display to a frequency. A power curve rather than a log one:
    /// log spacing crams almost everything into the bass end and leaves the rest of the width nearly
    /// flat, while this spreads mids and highs out so the whole spectrum has visible detail.
    /// </summary>
    private static float BinFrequency(float t) =>
        DisplayMinHz + (DisplayMaxHz - DisplayMinHz) * MathF.Pow(t, SpacingExponent);

    /// <summary>
    /// Converts a raw FFT magnitude to a 0-1 display level on a dB scale. The magnitude is first
    /// scaled to a real amplitude (4/n accounts for the single-sided spectrum and the Hann window's
    /// 0.5 coherent gain), so a full-scale sine lands at ~0dB = 1.0 and the floor sits at -72dB.
    /// A gentle high-frequency tilt compensates for music's natural bass-heavy energy, so hats and
    /// detail up top stay visible instead of being dwarfed by the kick.
    /// </summary>
    private static float NormalizeForDisplay(float magnitude, int fftSize, float centerHz)
    {
        var amplitude = magnitude * 4f / fftSize;
        if (amplitude <= 0f)
            return 0f;

        var tilt = Math.Clamp(MathF.Pow(centerHz / 250f, 0.38f), 1f, 4.5f);
        var db = 20f * MathF.Log10(amplitude * tilt);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
    }
}

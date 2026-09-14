using Stramp.Core.Dsp;

namespace Stramp.Core.Tests;

public class FftTests
{
    [Fact]
    public void Forward_PureSineWave_PeaksAtExpectedBin()
    {
        const int n = 1024;
        const int sampleRate = 44100;
        const float toneHz = 1000f;

        var real = new float[n];
        var imag = new float[n];
        for (var i = 0; i < n; i++)
            real[i] = MathF.Sin(2 * MathF.PI * toneHz * i / sampleRate);

        Fft.Forward(real, imag);

        var magnitude = new float[n / 2];
        for (var i = 0; i < magnitude.Length; i++)
            magnitude[i] = MathF.Sqrt(real[i] * real[i] + imag[i] * imag[i]);

        var peakBin = Array.IndexOf(magnitude, magnitude.Max());
        var expectedBin = (int)(toneHz / ((float)sampleRate / n));

        Assert.InRange(peakBin, expectedBin - 1, expectedBin + 1);
    }
}

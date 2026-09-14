namespace Stramp.Core.Dsp;

/// <summary>In-place iterative radix-2 Cooley-Tukey FFT. `real`/`imag` length must be a power of two.</summary>
public static class Fft
{
    public static void Forward(float[] real, float[] imag)
    {
        var n = real.Length;
        if (imag.Length != n)
            throw new ArgumentException("real and imag must be the same length");
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("length must be a power of two", nameof(real));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wLenRe = (float)Math.Cos(ang);
            var wLenIm = (float)Math.Sin(ang);

            for (var i = 0; i < n; i += len)
            {
                float wRe = 1f, wIm = 0f;
                var half = len / 2;
                for (var j = 0; j < half; j++)
                {
                    var uRe = real[i + j];
                    var uIm = imag[i + j];
                    var vRe = real[i + j + half] * wRe - imag[i + j + half] * wIm;
                    var vIm = real[i + j + half] * wIm + imag[i + j + half] * wRe;

                    real[i + j] = uRe + vRe;
                    imag[i + j] = uIm + vIm;
                    real[i + j + half] = uRe - vRe;
                    imag[i + j + half] = uIm - vIm;

                    var nextWRe = wRe * wLenRe - wIm * wLenIm;
                    var nextWIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextWRe;
                    wIm = nextWIm;
                }
            }
        }
    }
}

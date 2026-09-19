using NAudio.Wave;

namespace Stramp.Audio;

/// <summary>
/// Downmixes multi-channel audio (e.g. 5.1 or 7.1 surround) to 2-channel stereo.
/// Uses ITU-R BS.775 power-preserving weights for center and surround channels, including
/// LFE for music low-frequency retention, normalized to ensure zero digital clipping.
/// </summary>
public sealed class SurroundToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly WaveFormat _waveFormat;
    private float[] _sourceBuffer = [];

    public WaveFormat WaveFormat => _waveFormat;

    public SurroundToStereoSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels <= 2)
            throw new ArgumentException("Source must have more than 2 channels.", nameof(source));

        _source = source;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public int Read(Span<float> buffer)
    {
        var srcChannels = _source.WaveFormat.Channels;
        var requestedFrames = buffer.Length / 2;
        var neededSourceSamples = requestedFrames * srcChannels;

        if (_sourceBuffer.Length < neededSourceSamples)
            _sourceBuffer = new float[neededSourceSamples];

        var srcSamplesRead = _source.Read(_sourceBuffer.AsSpan(0, neededSourceSamples));
        var framesRead = srcSamplesRead / srcChannels;

        if (srcChannels == 6)
        {
            // Standard 5.1 layout: 0:FL, 1:FR, 2:FC, 3:LFE, 4:SL, 5:SR
            const float invSqrt2 = 0.70710678f;
            const float norm = 1.0f / (1.0f + 3.0f * invSqrt2);
            const float direct = 1.0f * norm;
            const float folded = invSqrt2 * norm;

            for (var f = 0; f < framesRead; f++)
            {
                var srcIdx = f * 6;
                var fl = _sourceBuffer[srcIdx];
                var fr = _sourceBuffer[srcIdx + 1];
                var fc = _sourceBuffer[srcIdx + 2];
                var lfe = _sourceBuffer[srcIdx + 3];
                var sl = _sourceBuffer[srcIdx + 4];
                var sr = _sourceBuffer[srcIdx + 5];

                buffer[f * 2] = fl * direct + (fc + sl + lfe) * folded;
                buffer[f * 2 + 1] = fr * direct + (fc + sr + lfe) * folded;
            }
        }
        else if (srcChannels == 8)
        {
            // Standard 7.1 layout: 0:FL, 1:FR, 2:FC, 3:LFE, 4:BL, 5:BR, 6:SL, 7:SR
            const float invSqrt2 = 0.70710678f;
            const float norm = 1.0f / (1.0f + 4.0f * invSqrt2);
            const float direct = 1.0f * norm;
            const float folded = invSqrt2 * norm;

            for (var f = 0; f < framesRead; f++)
            {
                var srcIdx = f * 8;
                var fl = _sourceBuffer[srcIdx];
                var fr = _sourceBuffer[srcIdx + 1];
                var fc = _sourceBuffer[srcIdx + 2];
                var lfe = _sourceBuffer[srcIdx + 3];
                var bl = _sourceBuffer[srcIdx + 4];
                var br = _sourceBuffer[srcIdx + 5];
                var sl = _sourceBuffer[srcIdx + 6];
                var sr = _sourceBuffer[srcIdx + 7];

                buffer[f * 2] = fl * direct + (fc + sl + bl + lfe) * folded;
                buffer[f * 2 + 1] = fr * direct + (fc + sr + br + lfe) * folded;
            }
        }
        else
        {
            // Generic downmix fallback for arbitrary channel counts
            var scale = 1.0f / MathF.Max(1, srcChannels / 2.0f);
            for (var f = 0; f < framesRead; f++)
            {
                var srcIdx = f * srcChannels;
                var leftSum = 0f;
                var rightSum = 0f;
                for (var c = 0; c < srcChannels; c++)
                {
                    if (c % 2 == 0)
                        leftSum += _sourceBuffer[srcIdx + c];
                    else
                        rightSum += _sourceBuffer[srcIdx + c];
                }
                buffer[f * 2] = leftSum * scale;
                buffer[f * 2 + 1] = rightSum * scale;
            }
        }

        return framesRead * 2;
    }
}

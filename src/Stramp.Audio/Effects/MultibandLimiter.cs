using NAudio.Dsp;

namespace Stramp.Audio.Effects;

/// <summary>
/// Holds the output below full scale, controlling each part of the spectrum separately.
///
/// A single wideband limiter has one lever: turn everything down. Feed it a bass-boosted modern
/// master and the bass peaks are what trigger it, so the mids and highs get pulled down for a
/// problem they had no part in. Measured on a real track with the "Music" preset, that cost 2 dB
/// at 2-6 kHz and 1.6 dB above 6 kHz, and left the result quieter overall than not processing at
/// all — the same complaint as an equalizer that turns the whole mix down when you lift the bass.
///
/// Splitting first means the boosted band absorbs its own limiting. Bass that asks for more than
/// there is room for gets held back; everything above it is left alone.
///
/// The crossovers are Linkwitz-Riley: two cascaded Butterworth sections, chosen because their
/// low and high halves sum back to a flat magnitude response. The low band is run through an
/// allpass matching the upper crossover so all three bands stay phase-aligned, which is what makes
/// the sum flat rather than notched at the crossover points.
/// </summary>
internal sealed class MultibandLimiter
{
    /// <summary>Peak level the summed output is held to, a hair under full scale.</summary>
    private const float Ceiling = 0.999f;

    /// <summary>Split points. Low is where boosted bass lives; high is where air lives.</summary>
    private const float LowCrossoverHz = 200;
    private const float HighCrossoverHz = 3000;

    /// <summary>Per-band release. Slower on the bass, whose waveform is long enough that a fast
    /// release would follow individual cycles and distort it.</summary>
    private static readonly float[] BandReleaseSeconds = [0.30f, 0.15f, 0.08f];

    /// <summary>Release for the safety limiter on the summed output.</summary>
    private const float OutputReleaseSeconds = 0.20f;

    private readonly int _channels;
    private readonly BiQuadFilter[,] _lowSplit;      // [channel, section]
    private readonly BiQuadFilter[,] _lowSplitHigh;
    private readonly BiQuadFilter[,] _highSplit;
    private readonly BiQuadFilter[,] _highSplitHigh;
    private readonly BiQuadFilter[,] _lowAllpass;
    private readonly float[] _bandRelease;
    private readonly float[] _bandGain;
    private readonly float _outputRelease;
    private float _outputGain = 1;

    private readonly float[] _low;
    private readonly float[] _mid;
    private readonly float[] _high;

    public MultibandLimiter(int sampleRate, int channels)
    {
        _channels = channels;
        _low = new float[channels];
        _mid = new float[channels];
        _high = new float[channels];

        _lowSplit = new BiQuadFilter[channels, 2];
        _lowSplitHigh = new BiQuadFilter[channels, 2];
        _highSplit = new BiQuadFilter[channels, 2];
        _highSplitHigh = new BiQuadFilter[channels, 2];
        _lowAllpass = new BiQuadFilter[channels, 2];

        for (var channel = 0; channel < channels; channel++)
            for (var section = 0; section < 2; section++)
            {
                _lowSplit[channel, section] = BiQuadFilter.LowPassFilter(sampleRate, LowCrossoverHz, 0.7071f);
                _lowSplitHigh[channel, section] = BiQuadFilter.HighPassFilter(sampleRate, LowCrossoverHz, 0.7071f);
                _highSplit[channel, section] = BiQuadFilter.LowPassFilter(sampleRate, HighCrossoverHz, 0.7071f);
                _highSplitHigh[channel, section] = BiQuadFilter.HighPassFilter(sampleRate, HighCrossoverHz, 0.7071f);
                _lowAllpass[channel, section] = BiQuadFilter.AllPassFilter(sampleRate, HighCrossoverHz, 0.7071f);
            }

        _bandRelease = new float[BandReleaseSeconds.Length];
        _bandGain = new float[BandReleaseSeconds.Length];
        for (var band = 0; band < _bandRelease.Length; band++)
        {
            _bandRelease[band] = 1 - MathF.Exp(-1f / (BandReleaseSeconds[band] * sampleRate));
            _bandGain[band] = 1;
        }

        _outputRelease = 1 - MathF.Exp(-1f / (OutputReleaseSeconds * sampleRate));
    }

    public void Reset()
    {
        foreach (var filter in _lowSplit) filter.ResetState();
        foreach (var filter in _lowSplitHigh) filter.ResetState();
        foreach (var filter in _highSplit) filter.ResetState();
        foreach (var filter in _highSplitHigh) filter.ResetState();
        foreach (var filter in _lowAllpass) filter.ResetState();

        for (var band = 0; band < _bandGain.Length; band++)
            _bandGain[band] = 1;
        _outputGain = 1;
    }

    /// <summary>
    /// Limits in place. Returns true if a frame had to be silenced for not being finite, which
    /// tells the caller to flush its state.
    /// </summary>
    public bool Process(Span<float> buffer, int count, int channels)
    {
        var sawNonFinite = false;

        for (var frame = 0; frame + channels <= count; frame += channels)
        {
            float lowPeak = 0, midPeak = 0, highPeak = 0;

            for (var channel = 0; channel < channels && channel < _channels; channel++)
            {
                var sample = buffer[frame + channel];

                // Split at 200 Hz, then split what is above it again at 3 kHz.
                var low = _lowSplit[channel, 1].Transform(_lowSplit[channel, 0].Transform(sample));
                var upper = _lowSplitHigh[channel, 1].Transform(_lowSplitHigh[channel, 0].Transform(sample));
                var mid = _highSplit[channel, 1].Transform(_highSplit[channel, 0].Transform(upper));
                var high = _highSplitHigh[channel, 1].Transform(_highSplitHigh[channel, 0].Transform(upper));

                // The low band skipped the second crossover, so it needs that crossover's phase
                // shift applied on its own for the three to sum back flat.
                low = _lowAllpass[channel, 1].Transform(_lowAllpass[channel, 0].Transform(low));

                _low[channel] = low;
                _mid[channel] = mid;
                _high[channel] = high;

                lowPeak = MathF.Max(lowPeak, MathF.Abs(low));
                midPeak = MathF.Max(midPeak, MathF.Abs(mid));
                highPeak = MathF.Max(highPeak, MathF.Abs(high));
            }

            if (!float.IsFinite(lowPeak) || !float.IsFinite(midPeak) || !float.IsFinite(highPeak))
            {
                buffer.Slice(frame, channels).Clear();
                Reset();
                sawNonFinite = true;
                continue;
            }

            // Each band is allowed the full ceiling. They rarely peak together, and holding each
            // to a third of it would cost far more level than the occasional overlap costs.
            UpdateBandGain(0, lowPeak);
            UpdateBandGain(1, midPeak);
            UpdateBandGain(2, highPeak);

            var summedPeak = 0f;
            for (var channel = 0; channel < channels && channel < _channels; channel++)
            {
                var summed = _low[channel] * _bandGain[0]
                           + _mid[channel] * _bandGain[1]
                           + _high[channel] * _bandGain[2];
                buffer[frame + channel] = summed;
                summedPeak = MathF.Max(summedPeak, MathF.Abs(summed));
            }

            // Safety net for the frames where the bands do line up. This is wideband, but it now
            // only has to catch what three separate limiters already brought most of the way down.
            var required = summedPeak > Ceiling ? Ceiling / summedPeak : 1f;
            _outputGain = MathF.Min(required, _outputGain + (1 - _outputGain) * _outputRelease);

            if (_outputGain < 1)
                for (var channel = 0; channel < channels; channel++)
                    buffer[frame + channel] *= _outputGain;
        }

        return sawNonFinite;
    }

    private void UpdateBandGain(int band, float peak)
    {
        var required = peak > Ceiling ? Ceiling / peak : 1f;
        _bandGain[band] = MathF.Min(required, _bandGain[band] + (1 - _bandGain[band]) * _bandRelease[band]);
    }
}

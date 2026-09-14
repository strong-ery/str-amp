namespace Stramp.Audio.Effects;

/// <summary>
/// Holds the output below full scale without clipping it.
///
/// Instant attack, exponential release: the gain drops exactly as far as the current frame needs
/// and no further, then eases back toward unity. Because the reduction is computed from the frame
/// it is applied to, the output cannot exceed the ceiling at all — there is no overshoot for a
/// later stage to clip. Anything already below the ceiling passes through multiplied by one, so a
/// track that never approaches full scale is untouched.
///
/// One gain is computed from the loudest channel and applied to all of them, so limiting never
/// shifts the stereo image.
/// </summary>
internal sealed class PeakLimiter
{
    /// <summary>Peak level the output is held to, a hair under full scale.</summary>
    private const float Ceiling = 0.999f;

    /// <summary>Time constant for letting go again after a peak.</summary>
    private const float ReleaseSeconds = 0.25f;

    private readonly float _release;
    private float _gain = 1;

    public PeakLimiter(int sampleRate) =>
        _release = 1 - MathF.Exp(-1f / (ReleaseSeconds * sampleRate));

    public void Reset() => _gain = 1;

    /// <summary>
    /// Limits in place. Returns true if a frame had to be silenced because it was not finite —
    /// a diverged filter or a bad decode — which tells the caller to flush its state.
    /// </summary>
    public bool Process(Span<float> buffer, int count, int channels)
    {
        var sawNonFinite = false;

        for (var frame = 0; frame + channels <= count; frame += channels)
        {
            var peak = 0f;
            for (var channel = 0; channel < channels; channel++)
                peak = MathF.Max(peak, MathF.Abs(buffer[frame + channel]));

            if (!float.IsFinite(peak))
            {
                buffer.Slice(frame, channels).Clear();
                _gain = 1;
                sawNonFinite = true;
                continue;
            }

            var required = peak > Ceiling ? Ceiling / peak : 1f;
            _gain = MathF.Min(required, _gain + (1 - _gain) * _release);

            if (_gain < 1)
                for (var channel = 0; channel < channels; channel++)
                    buffer[frame + channel] *= _gain;
        }

        return sawNonFinite;
    }
}

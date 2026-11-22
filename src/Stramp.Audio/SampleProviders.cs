using NAudio.Wave;

namespace Stramp.Audio;

/// <summary>Applies a constant gain with a short smoothing ramp when it changes mid-track.</summary>
internal sealed class GainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _smoothingFactor;
    private float _currentGain;
    private float _targetGain;
    private int _snapToTarget;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public GainSampleProvider(ISampleProvider source, double gainDb)
    {
        _source = source;
        _currentGain = DbToLinear(gainDb);
        _targetGain = _currentGain;
        _smoothingFactor = 1 - MathF.Exp(-1 / (0.02f * WaveFormat.SampleRate));
    }

    /// <param name="immediate">Skip the ramp: the next sample read already has the new gain.</param>
    public void SetGainDb(double gainDb, bool immediate = false)
    {
        Volatile.Write(ref _targetGain, DbToLinear(gainDb));
        if (immediate)
            Volatile.Write(ref _snapToTarget, 1);
    }

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);
        var target = Volatile.Read(ref _targetGain);
        if (Interlocked.Exchange(ref _snapToTarget, 0) == 1)
            _currentGain = target;
        var channels = WaveFormat.Channels;

        for (var frame = 0; frame < read; frame += channels)
        {
            _currentGain += (target - _currentGain) * _smoothingFactor;
            if (MathF.Abs(target - _currentGain) < 0.000001f)
                _currentGain = target;

            for (var channel = 0; channel < channels && frame + channel < read; channel++)
                buffer[frame + channel] *= _currentGain;
        }

        return read;
    }

    private static float DbToLinear(double gainDb) =>
        (float)Math.Pow(10, Math.Clamp(gainDb, -60, 24) / 20);
}

/// <summary>
/// Replaces every channel of a frame with their average, so both ears hear the same thing.
///
/// Averaging rather than summing keeps the level where it was: identical channels stay at their
/// own level instead of arriving 6 dB louder, and channels that disagree partially cancel, which
/// is what a mono fold-down is for hearing in the first place.
///
/// The switch fades over the same short ramp the gain stage uses rather than snapping, because
/// flipping it mid-track is otherwise a step change in every channel at once: a click.
/// </summary>
internal sealed class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _smoothingFactor;
    private float _blend;
    private float _targetBlend;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public MonoDownmixSampleProvider(ISampleProvider source, bool enabled)
    {
        _source = source;
        _blend = enabled ? 1 : 0;
        _targetBlend = _blend;
        _smoothingFactor = 1 - MathF.Exp(-1 / (0.02f * WaveFormat.SampleRate));
    }

    public void SetEnabled(bool enabled) =>
        Volatile.Write(ref _targetBlend, enabled ? 1 : 0);

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);
        var channels = WaveFormat.Channels;
        var target = Volatile.Read(ref _targetBlend);

        // Off and fully faded out, which is the default and the common case: nothing to do.
        if (channels < 2 || (target == 0 && _blend == 0))
            return read;

        for (var frame = 0; frame + channels <= read; frame += channels)
        {
            _blend += (target - _blend) * _smoothingFactor;
            if (MathF.Abs(target - _blend) < 0.000001f)
                _blend = target;

            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
                sum += buffer[frame + channel];

            var mean = sum / channels;
            for (var channel = 0; channel < channels; channel++)
                buffer[frame + channel] += (mean - buffer[frame + channel]) * _blend;
        }

        return read;
    }
}

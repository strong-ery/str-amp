namespace Stramp.Audio.Effects;

/// <summary>
/// A control value that slides to its target instead of jumping. Every effect amount here is a
/// gain of some kind, and stepping a gain between buffers puts a discontinuity straight into the
/// signal — the zipper noise you hear when a badly-written plugin's slider is dragged.
/// </summary>
internal sealed class SmoothedParameter
{
    private const float DefaultGlideSeconds = 0.03f;

    /// <summary>Below this the value is snapped, so it actually reaches the target.</summary>
    private const float Settled = 1e-6f;

    private readonly float _coefficient;
    private float _current;
    private float _target;

    public SmoothedParameter(int sampleRate, float initial = 0, float glideSeconds = DefaultGlideSeconds)
    {
        _current = initial;
        _target = initial;
        _coefficient = 1 - MathF.Exp(-1f / (MathF.Max(glideSeconds, 1e-4f) * sampleRate));
    }

    /// <summary>The value right now, without advancing.</summary>
    public float Current => _current;

    /// <summary>True once the value has arrived, which lets callers skip idle work.</summary>
    public bool IsSettled => _current == _target;

    public void SetTarget(float value) => _target = value;

    /// <summary>Jumps straight to a value. For track changes, where there is nothing to glide from.</summary>
    public void SnapTo(float value)
    {
        _current = value;
        _target = value;
    }

    /// <summary>Advances one sample (or one frame) and returns the value to use for it.</summary>
    public float Next()
    {
        if (_current != _target)
        {
            _current += (_target - _current) * _coefficient;
            if (MathF.Abs(_target - _current) < Settled)
                _current = _target;
        }
        return _current;
    }
}

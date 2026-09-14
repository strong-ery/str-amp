namespace Stramp.Core.Dsp;

/// <summary>Result of feeding one frame to a <see cref="BeatDetector"/>.</summary>
/// <param name="IsHit">True only on the frame an onset is detected.</param>
/// <param name="Strength">
/// How hard the hit was, 0-1, based on how far the frame overshot the running average. Lets
/// callers scale an animation to the punch instead of firing a fixed-size pulse every time.
/// </param>
public readonly record struct BeatHit(bool IsHit, float Strength);

/// <summary>
/// Simple adaptive onset ("beat") detector: tracks a slow-moving average of a band's energy and
/// fires a hit when the current frame spikes well above it, with a refractory period so one loud
/// hit doesn't retrigger on every following frame while it decays.
/// </summary>
public sealed class BeatDetector
{
    private readonly double _sensitivity;
    private readonly int _refractoryFrames;
    private double _average;
    private bool _seeded;
    private int _framesSinceHit;

    public BeatDetector(double sensitivity = 1.5, int refractoryFrames = 6)
    {
        _sensitivity = sensitivity;
        _refractoryFrames = refractoryFrames;
        _framesSinceHit = refractoryFrames;
    }

    /// <summary>Feed one frame's band energy.</summary>
    public BeatHit Update(float energy)
    {
        if (_framesSinceHit < int.MaxValue - 1)
            _framesSinceHit++;

        if (!_seeded)
        {
            _average = energy;
            _seeded = true;
        }

        var isHit = energy > _average * _sensitivity
            && energy > 1e-4
            && _framesSinceHit >= _refractoryFrames;

        var strength = 0f;
        if (isHit)
        {
            _framesSinceHit = 0;

            // How far past the threshold did it land? A hit that barely qualifies reads soft,
            // one that massively overshoots reads full strength.
            var ratio = energy / Math.Max(_average, 1e-6);
            var overshoot = (ratio - _sensitivity) / (_sensitivity * 1.5);
            strength = (float)Math.Clamp(0.35 + overshoot, 0.35, 1.0);
        }

        const double alpha = 0.08;
        _average = _average * (1 - alpha) + energy * alpha;

        return new BeatHit(isHit, strength);
    }
}

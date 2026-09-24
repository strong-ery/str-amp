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
    private readonly double _refractorySeconds;
    private double _average;
    private bool _seeded;
    private double _secondsSinceHit;

    public BeatDetector(double sensitivity = 1.5, int refractoryFrames = 6)
        : this(sensitivity, refractoryFrames / 60.0)
    {
    }

    public BeatDetector(double sensitivity, double refractorySeconds)
    {
        _sensitivity = sensitivity;
        _refractorySeconds = refractorySeconds;
        _secondsSinceHit = refractorySeconds;
    }

    /// <summary>Feed one frame's band energy assuming a default 60fps tick.</summary>
    public BeatHit Update(float energy) => Update(energy, 1.0 / 60.0);

    /// <summary>Feed one frame's band energy with the elapsed delta time since last frame.</summary>
    public BeatHit Update(float energy, double dt)
    {
        _secondsSinceHit += dt;

        if (!_seeded)
        {
            _average = energy;
            _seeded = true;
        }

        var isHit = energy > _average * _sensitivity
            && energy > 1e-4
            && _secondsSinceHit >= _refractorySeconds;

        var strength = 0f;
        if (isHit)
        {
            _secondsSinceHit = 0;

            // How far past the threshold did it land? A hit that barely qualifies reads soft,
            // one that massively overshoots reads full strength.
            var ratio = energy / Math.Max(_average, 1e-6);
            var overshoot = (ratio - _sensitivity) / (_sensitivity * 1.5);
            strength = (float)Math.Clamp(0.35 + overshoot, 0.35, 1.0);
        }

        var dtRatio = dt * 60.0;
        var alpha = 1.0 - Math.Pow(1.0 - 0.08, dtRatio);
        _average = _average * (1.0 - alpha) + energy * alpha;

        return new BeatHit(isHit, strength);
    }
}

namespace Stramp.App.Services;

/// <summary>
/// Eases a 0-1 "how shown" fraction toward fully visible or fully collapsed. Pure state/math:
/// the caller multiplies the fraction by whatever actually needs to shrink - a panel's real
/// Height (so the space it occupied is genuinely reclaimed, not just painted over) and/or its
/// Opacity - rather than this animator knowing about layout at all.
/// </summary>
public sealed class FadeCollapseAnimator
{
    private const double DurationMs = 250.0;

    private bool _isAnimating;
    private double _startFraction = 1;
    private double _targetFraction = 1;
    private DateTime _startTime;

    public double CurrentFraction { get; private set; } = 1;

    public bool IsAnimating => _isAnimating;

    public void Start(bool visible)
    {
        _startFraction = CurrentFraction;
        _targetFraction = visible ? 1 : 0;
        _startTime = DateTime.UtcNow;
        _isAnimating = true;
    }

    /// <summary>
    /// Advances the animation by one frame and returns the current (or final) fraction.
    /// <paramref name="finished"/> is true once the target is reached, at which point the
    /// animator stops itself.
    /// </summary>
    public double Step(out bool finished)
    {
        finished = false;
        if (!_isAnimating)
            return CurrentFraction;

        var elapsed = (DateTime.UtcNow - _startTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / DurationMs, 0.0, 1.0);
        var easedT = t * t * (3 - 2 * t);

        CurrentFraction = _startFraction + (_targetFraction - _startFraction) * easedT;

        if (t >= 1.0)
        {
            _isAnimating = false;
            finished = true;
            CurrentFraction = _targetFraction;
        }

        return CurrentFraction;
    }
}

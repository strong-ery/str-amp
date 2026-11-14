namespace Stramp.App.Services;

/// <summary>
/// Eases a panel's opacity and vertical scale together toward shown or hidden, so it visually
/// sinks away rather than just turning transparent in place. Pure state/math: the caller owns
/// applying the stepped opacity/scale to the actual control (with a bottom-anchored
/// RenderTransformOrigin so the shrink reads as sinking into the edge it sits against).
/// </summary>
public sealed class FadeCollapseAnimator
{
    private const double DurationMs = 250.0;

    private bool _isAnimating;
    private double _startOpacity = 1;
    private double _targetOpacity = 1;
    private DateTime _startTime;

    public void Start(bool visible)
    {
        _startOpacity = CurrentOpacity;
        _targetOpacity = visible ? 1 : 0;
        _startTime = DateTime.UtcNow;
        _isAnimating = true;
    }

    /// <summary>The opacity this animator last produced, used as the next Start's baseline so a
    /// direction reversed mid-glide eases from where it actually is rather than jumping.</summary>
    public double CurrentOpacity { get; private set; } = 1;

    /// <summary>
    /// Advances the animation by one frame and returns the current (or final) opacity and the
    /// vertical scale to pair it with. <paramref name="finished"/> is true once the target is
    /// reached, at which point the animator stops itself.
    /// </summary>
    public (double Opacity, double ScaleY) Step(out bool finished)
    {
        finished = false;
        if (!_isAnimating)
            return (CurrentOpacity, OpacityToScale(CurrentOpacity));

        var elapsed = (DateTime.UtcNow - _startTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / DurationMs, 0.0, 1.0);
        var easedT = t * t * (3 - 2 * t);

        CurrentOpacity = _startOpacity + (_targetOpacity - _startOpacity) * easedT;

        if (t >= 1.0)
        {
            _isAnimating = false;
            finished = true;
            CurrentOpacity = _targetOpacity;
        }

        return (CurrentOpacity, OpacityToScale(CurrentOpacity));
    }

    /// <summary>Scale follows opacity on the same curve, so the panel is fully flattened exactly
    /// when it's fully transparent and fully sized exactly when fully opaque.</summary>
    private static double OpacityToScale(double opacity) => Math.Max(0.0001, opacity);
}

namespace Stramp.App.Services;

/// <summary>
/// Eases the library/up-next sidebar column widths toward their targets over a fixed duration,
/// using a smoothstep so the columns glide to size instead of snapping. Pure state/math — the
/// caller owns applying the stepped widths to the actual grid columns.
/// </summary>
public sealed class ColumnResizeAnimator
{
    private const double DurationMs = 250.0;

    private bool _isAnimating;
    private double _startLeft;
    private double _targetLeft;
    private double _startRight;
    private double _targetRight;
    private DateTime _startTime;

    public bool IsAnimating => _isAnimating;

    public void Start(double startLeft, double targetLeft, double startRight, double targetRight)
    {
        _startLeft = startLeft;
        _targetLeft = targetLeft;
        _startRight = startRight;
        _targetRight = targetRight;
        _startTime = DateTime.UtcNow;
        _isAnimating = true;
    }

    public void Stop() => _isAnimating = false;

    /// <summary>
    /// Advances the animation by one frame and returns the current (or final) widths.
    /// <paramref name="finished"/> is true once the target has been reached, at which point the
    /// animator stops itself.
    /// </summary>
    public (double Left, double Right) Step(out bool finished)
    {
        finished = false;
        if (!_isAnimating)
            return (_targetLeft, _targetRight);

        var elapsed = (DateTime.UtcNow - _startTime).TotalMilliseconds;
        double t = Math.Clamp(elapsed / DurationMs, 0.0, 1.0);
        double easedT = t * t * (3 - 2 * t);

        double left = _startLeft + (_targetLeft - _startLeft) * easedT;
        double right = _startRight + (_targetRight - _startRight) * easedT;

        if (t >= 1.0)
        {
            _isAnimating = false;
            finished = true;
            return (_targetLeft, _targetRight);
        }

        return (left, right);
    }
}

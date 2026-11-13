namespace Stramp.App.Services;

/// <summary>
/// Owns the two pieces of lyrics-panel animation state: the panel's open/close width tween (eased
/// pixel width toward 0 or half the card) and the auto-scroll that glides the list toward the
/// active line, pausing itself for a few seconds after the user scrolls by hand. Kept together
/// because both are what "the lyrics panel is animating" means to callers. Pure state/math — the
/// caller owns applying the stepped values to the actual grid column and scroll viewer.
/// </summary>
public sealed class LyricsScrollAnimator
{
    private const double PanelAnimDurationMs = 250.0;
    private const double ScrollEase = 0.16;
    private const double ScrollSettleDistance = 0.5;

    // Panel open/close width tween.
    private bool _isPanelAnimating;
    private double _panelStartPixels;
    private double _panelTargetPixels;
    private DateTime _panelStartTime;
    private bool _panelShouldBeOpen;

    // Scroll-to-active-line easing.
    private double? _scrollTarget;
    private double _manualScrollSecondsRemaining;

    public bool IsPanelAnimating => _isPanelAnimating;

    public bool IsManualScrollPauseActive => _manualScrollSecondsRemaining > 0;

    public void StartPanelAnimation(double startPixels, double targetPixels, bool shouldBeOpen)
    {
        _panelStartPixels = startPixels;
        _panelTargetPixels = targetPixels;
        _panelStartTime = DateTime.UtcNow;
        _panelShouldBeOpen = shouldBeOpen;
        _isPanelAnimating = true;
    }

    public void StopPanelAnimation() => _isPanelAnimating = false;

    /// <summary>
    /// Advances the panel width tween by one frame. <paramref name="finished"/> is true once the
    /// target width is reached, at which point the animator stops itself; <paramref name="shouldBeOpen"/>
    /// reports whether the panel should end up fully open or fully closed.
    /// </summary>
    public double StepPanelAnimation(out bool finished, out bool shouldBeOpen)
    {
        finished = false;
        shouldBeOpen = _panelShouldBeOpen;
        if (!_isPanelAnimating)
            return _panelTargetPixels;

        var elapsed = (DateTime.UtcNow - _panelStartTime).TotalMilliseconds;
        double t = Math.Clamp(elapsed / PanelAnimDurationMs, 0.0, 1.0);
        double easedT = t * t * (3 - 2 * t);
        double current = _panelStartPixels + (_panelTargetPixels - _panelStartPixels) * easedT;

        if (t >= 1.0)
        {
            _isPanelAnimating = false;
            finished = true;
            return _panelTargetPixels;
        }

        return current;
    }

    /// <summary>Aims the auto-scroll at a new offset; a glide already in progress retargets instead of jumping.</summary>
    public void SetScrollTarget(double target) => _scrollTarget = target;

    public void ClearScrollTarget() => _scrollTarget = null;

    /// <summary>Parks the auto-scroll for a few seconds after the user scrolls by hand.</summary>
    public void BeginManualScrollPause(double seconds) => _manualScrollSecondsRemaining = seconds;

    public void ClearManualScrollPause() => _manualScrollSecondsRemaining = 0;

    /// <summary>Ticks the manual-scroll pause down. Returns true the instant it expires, so the
    /// caller can re-aim the auto-scroll at the active line before easing toward it this frame.</summary>
    public bool TickManualPause(double dt)
    {
        if (_manualScrollSecondsRemaining <= 0)
            return false;

        _manualScrollSecondsRemaining -= dt;
        if (_manualScrollSecondsRemaining > 0)
            return false;

        _manualScrollSecondsRemaining = 0;
        return true;
    }

    /// <summary>Moves a fraction of the remaining distance toward the current scroll target, if any.</summary>
    public double? EaseTowardTarget(double dt, double currentOffset)
    {
        if (_scrollTarget is not { } target)
            return null;

        var remaining = target - currentOffset;
        if (Math.Abs(remaining) < ScrollSettleDistance)
        {
            _scrollTarget = null;
            return target;
        }

        var dtRatio = dt * 60.0;
        var ease = 1.0 - Math.Pow(1.0 - ScrollEase, dtRatio);
        return currentOffset + remaining * ease;
    }
}

namespace Stramp.App.Services;

/// <summary>
/// Turns a scroll surface's wheel input into a glide instead of Avalonia's default instant
/// per-notch jump: each wheel tick nudges a target offset, which this eases toward every frame
/// using the same continuously-retargetable exponential ease as <see cref="LyricsScrollAnimator"/>'s
/// auto-scroll. Pure state/math: the caller owns reading the scroll surface's offset/extent and
/// applying the stepped value back to it.
/// </summary>
public sealed class SmoothScrollAnimator
{
    private const double Ease = 0.25;
    private const double SettleDistance = 0.5;

    /// <summary>Pixels the target moves per unit of wheel delta (matches Avalonia's own default).</summary>
    private const double PixelsPerWheelNotch = 120.0;

    private double? _target;

    /// <summary>
    /// Nudges the target offset by one wheel tick, clamped to <paramref name="maxOffset"/>. Safe to
    /// call again before the previous glide settles: it retargets rather than snapping back.
    /// </summary>
    public void HandleWheel(double wheelDeltaY, double currentOffset, double maxOffset)
    {
        var baseOffset = _target ?? currentOffset;
        var next = baseOffset - wheelDeltaY * PixelsPerWheelNotch;
        _target = Math.Clamp(next, 0, Math.Max(0, maxOffset));
    }

    /// <summary>Moves a fraction of the remaining distance toward the current target, if any.</summary>
    public double? Step(double dt, double currentOffset)
    {
        if (_target is not { } target)
            return null;

        var remaining = target - currentOffset;
        if (Math.Abs(remaining) < SettleDistance)
        {
            _target = null;
            return target;
        }

        var dtRatio = dt * 60.0;
        var ease = 1.0 - Math.Pow(1.0 - Ease, dtRatio);
        return currentOffset + remaining * ease;
    }

    /// <summary>Drops any in-flight target, e.g. when the content being scrolled is swapped out.</summary>
    public void Reset() => _target = null;
}

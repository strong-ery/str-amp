using Avalonia.Controls;
using Avalonia.Input;
using Stramp.App.Services;

namespace Stramp.App.Views;

/// <summary>
/// Fullscreen-only niceties that don't belong in the main chrome-toggling logic: the transport
/// controls shrink away after the mouse sits still for a few seconds and reappear the instant it
/// moves, the way a fullscreen video player's controls do.
/// </summary>
public partial class MainWindow
{
    private const double ControlsIdleHideDelaySeconds = 2.5;

    private readonly FadeCollapseAnimator _controlsAnimator = new();
    private double _controlsIdleSecondsRemaining = ControlsIdleHideDelaySeconds;
    private bool _areControlsHidden;
    private double _playerControlsFullHeight;

    /// <summary>
    /// Tracks the panel's natural height whenever it's at its real (un-overridden) size, so
    /// there's a real value to shrink from and grow back to - including across a resize or a
    /// switch between the normal and compact control bar layouts while fullscreen is active.
    /// </summary>
    private void OnPlayerControlsSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (double.IsNaN(PlayerControlsPanel.Height))
            _playerControlsFullHeight = e.NewSize.Height;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _controlsIdleSecondsRemaining = ControlsIdleHideDelaySeconds;
        if (_areControlsHidden)
            SetControlsVisible(true);
    }

    /// <summary>Called once per animation frame; outside fullscreen the controls just stay shown.</summary>
    private void StepControlsIdleTimer(double dt)
    {
        if (!_isImmersiveFullScreen)
        {
            if (_areControlsHidden)
                SetControlsVisible(true);
        }
        else if (!_areControlsHidden)
        {
            _controlsIdleSecondsRemaining -= dt;
            if (_controlsIdleSecondsRemaining <= 0)
                SetControlsVisible(false);
        }

        // Skip once fully shown and settled: touching Height every frame here would immediately
        // undo the NaN below and pin the panel to a stale cached height instead of letting
        // OnPlayerControlsSizeChanged track its natural size again.
        if (!_controlsAnimator.IsAnimating && _controlsAnimator.CurrentFraction >= 1)
            return;

        var fraction = _controlsAnimator.Step(out var finished);
        PlayerControlsPanel.Opacity = fraction;
        PlayerControlsPanel.Height = finished && fraction >= 1 ? double.NaN : _playerControlsFullHeight * fraction;
    }

    private void SetControlsVisible(bool visible)
    {
        _areControlsHidden = !visible;
        PlayerControlsPanel.IsHitTestVisible = visible;
        _controlsAnimator.Start(visible);

        // The cursor going idle-invisible alongside the controls is what actually sells "nothing
        // is happening here right now", the same way a fullscreen video player does it.
        Cursor = visible ? Cursor.Default : new Cursor(StandardCursorType.None);
    }
}

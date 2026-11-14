using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

        var (opacity, scaleY) = _controlsAnimator.Step(out _);
        PlayerControlsPanel.Opacity = opacity;
        if (PlayerControlsPanel.RenderTransform is ScaleTransform scale)
            scale.ScaleY = scaleY;
    }

    private void SetControlsVisible(bool visible)
    {
        _areControlsHidden = !visible;
        PlayerControlsPanel.IsHitTestVisible = visible;
        _controlsAnimator.Start(visible);
    }
}

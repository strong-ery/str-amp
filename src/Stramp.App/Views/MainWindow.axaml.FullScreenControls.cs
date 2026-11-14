using Avalonia.Controls;
using Avalonia.Input;

namespace Stramp.App.Views;

/// <summary>
/// Fullscreen-only niceties that don't belong in the main chrome-toggling logic: the transport
/// controls fade out after the mouse sits still for a few seconds and reappear the instant it
/// moves, the way a fullscreen video player's controls do.
/// </summary>
public partial class MainWindow
{
    private const double ControlsIdleHideDelaySeconds = 2.5;

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
            return;
        }

        if (_areControlsHidden)
            return;

        _controlsIdleSecondsRemaining -= dt;
        if (_controlsIdleSecondsRemaining <= 0)
            SetControlsVisible(false);
    }

    private void SetControlsVisible(bool visible)
    {
        _areControlsHidden = !visible;
        PlayerControlsPanel.Opacity = visible ? 1 : 0;
        PlayerControlsPanel.IsHitTestVisible = visible;
    }
}

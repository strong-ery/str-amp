using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;

namespace Stramp.App.Controls;

/// <summary>
/// Seek bar that draws the track's waveform instead of a plain rail: played portion in the accent
/// color, the rest dimmed, with a playhead line. Click or drag anywhere to scrub. Falls back to a
/// flat bar until the waveform for the current track has been decoded.
/// </summary>
public sealed class WaveformSeekBar : Control, ICustomHitTest
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(PlayedBrush));

    public static readonly StyledProperty<IBrush?> RemainingBrushProperty =
        AvaloniaProperty.Register<WaveformSeekBar, IBrush?>(nameof(RemainingBrush));

    /// <summary>Fired continuously while scrubbing, with a 0-1 position.</summary>
    public event Action<double>? Scrubbing;

    /// <summary>Fired once when the scrub gesture ends, with the final 0-1 position.</summary>
    public event Action<double>? ScrubCompleted;

    private float[]? _waveform;
    private bool _isDragging;

    static WaveformSeekBar()
    {
        AffectsRender<WaveformSeekBar>(ProgressProperty, PlayedBrushProperty, RemainingBrushProperty);
    }

    public WaveformSeekBar()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Playback position as a 0-1 fraction of the track.</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush? RemainingBrush
    {
        get => GetValue(RemainingBrushProperty);
        set => SetValue(RemainingBrushProperty, value);
    }

    public void SetWaveform(float[]? waveform)
    {
        _waveform = waveform;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _isDragging = true;
        e.Pointer.Capture(this);
        Scrubbing?.Invoke(PositionFrom(e));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
            Scrubbing?.Invoke(PositionFrom(e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging)
            return;

        _isDragging = false;
        e.Pointer.Capture(null);
        ScrubCompleted?.Invoke(PositionFrom(e));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_isDragging)
            return;

        _isDragging = false;
        ScrubCompleted?.Invoke(Math.Clamp(Progress, 0, 1));
    }

    private double PositionFrom(PointerEventArgs e) =>
        Bounds.Width <= 0 ? 0 : Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);

    /// <summary>
    /// Treat the whole control as the seek target. Without this, transparent space around short
    /// waveform bars can fall through hit testing and forces the pointer onto a painted bar.
    /// </summary>
    public bool HitTest(Point point) =>
        point.X >= 0 && point.X <= Bounds.Width && point.Y >= 0 && point.Y <= Bounds.Height;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var played = (PlayedBrush as ISolidColorBrush)?.Color ?? Color.Parse("#7C5CFF");
        var remaining = (RemainingBrush as ISolidColorBrush)?.Color ?? Color.Parse("#808080");
        var progressX = Math.Clamp(Progress, 0, 1) * width;

        if (_waveform is null || _waveform.Length == 0)
        {
            DrawFlatRail(context, width, height, played, remaining, progressX);
            return;
        }

        var mid = height / 2;
        var barWidth = Math.Max(1.0, width / _waveform.Length);
        var playedBrush = new ImmutableSolidColorBrush(played);
        var remainingBrush = new ImmutableSolidColorBrush(remaining, 0.35);

        for (var i = 0; i < _waveform.Length; i++)
        {
            var x = i * width / _waveform.Length;
            // Keep a visible sliver even in near-silence so the bar reads as a track, not a gap.
            var amplitude = Math.Max(_waveform[i] * (height / 2 - 1), 1.0);
            var brush = x + barWidth <= progressX ? playedBrush : remainingBrush;

            context.FillRectangle(
                brush,
                new Rect(x, mid - amplitude, Math.Max(barWidth - 0.6, 0.6), amplitude * 2));
        }

        context.FillRectangle(
            new ImmutableSolidColorBrush(Lighten(played, 0.4)),
            new Rect(progressX - 1, 0, 2, height));
    }

    private static void DrawFlatRail(DrawingContext context, double width, double height, Color played, Color remaining, double progressX)
    {
        var railHeight = 4.0;
        var y = (height - railHeight) / 2;

        context.FillRectangle(
            new ImmutableSolidColorBrush(remaining, 0.35),
            new Rect(0, y, width, railHeight),
            2);
        context.FillRectangle(
            new ImmutableSolidColorBrush(played),
            new Rect(0, y, progressX, railHeight),
            2);
    }

    private static Color Lighten(Color c, double amount) => Color.FromArgb(
        c.A,
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));
}

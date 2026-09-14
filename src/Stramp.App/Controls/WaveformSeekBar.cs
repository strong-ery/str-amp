using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;

namespace Stramp.App.Controls;

/// <summary>
/// Seek bar that draws the track's waveform following Spicetify playback-bar-waveform logic:
/// discrete vertical bars with tiny gaps and a 2px center gap, filled with played accent color
/// on the left and remaining color on the right using clip masking. Hovering or scrubbing
/// moves the full-height playhead line and displays a formatted timestamp badge floating above.
/// </summary>
public sealed class WaveformSeekBar : Control, ICustomHitTest
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(Progress));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<WaveformSeekBar, double>(nameof(Duration));

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
    private bool _isHovered;
    private double _hoverX;

    static WaveformSeekBar()
    {
        AffectsRender<WaveformSeekBar>(ProgressProperty, DurationProperty, PlayedBrushProperty, RemainingBrushProperty);
    }

    public WaveformSeekBar()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = false;
    }

    /// <summary>Playback position as a 0-1 fraction of the track.</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Total duration of the track in seconds.</summary>
    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
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

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isHovered = true;
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isHovered = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _isDragging = true;
        _hoverX = e.GetPosition(this).X;
        e.Pointer.Capture(this);
        Scrubbing?.Invoke(PositionFrom(e));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverX = e.GetPosition(this).X;
        if (_isDragging)
            Scrubbing?.Invoke(PositionFrom(e));
        InvalidateVisual();
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
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_isDragging)
            return;

        _isDragging = false;
        ScrubCompleted?.Invoke(Math.Clamp(Progress, 0, 1));
        InvalidateVisual();
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
        var playedBrush = new ImmutableSolidColorBrush(played);
        var remainingBrush = new ImmutableSolidColorBrush(remaining, 0.35);

        var handleX = (_isHovered || _isDragging) ? Math.Clamp(_hoverX, 0, width) : progressX;

        if (_waveform is null || _waveform.Length == 0)
        {
            DrawFlatRail(context, width, height, playedBrush, remainingBrush, progressX);
        }
        else
        {
            DrawDiscreteWaveformBars(context, width, height, playedBrush, remainingBrush, progressX);
        }

        // Full-height 2px vertical playhead line handle
        var handleBrush = new ImmutableSolidColorBrush(Lighten(played, 0.4));
        context.FillRectangle(handleBrush, new Rect(handleX - 1, 0, 2, height), 1.0f);

        // Hover / scrub timestamp badge floating above the playhead
        if (_isHovered || _isDragging)
        {
            DrawTimestampBadge(context, width, handleX);
        }
    }

    private void DrawDiscreteWaveformBars(
        DrawingContext context,
        double width,
        double height,
        IBrush playedBrush,
        IBrush remainingBrush,
        double progressX)
    {
        var count = _waveform!.Length;
        if (count == 0)
            return;

        // Target 3px step per bar (~2px bar width, 1px gap)
        const double targetStep = 3.0;
        var barCount = Math.Max(10, (int)Math.Round(width / targetStep));
        var step = width / barCount;
        var barWidth = Math.Max(1.0, step - 1.0);

        const double centerGap = 2.0;
        var mid = height / 2.0;
        var maxHalfHeight = Math.Max(1.0, (height - centerGap) / 2.0);

        // Draw played bars clipped to [0, progressX]
        if (progressX > 0)
        {
            using (context.PushClip(new Rect(0, 0, progressX, height)))
            {
                RenderBarSet(context, playedBrush, barCount, step, barWidth, count, mid, maxHalfHeight, centerGap);
            }
        }

        // Draw remaining bars clipped to [progressX, width - progressX]
        if (progressX < width)
        {
            using (context.PushClip(new Rect(progressX, 0, width - progressX, height)))
            {
                RenderBarSet(context, remainingBrush, barCount, step, barWidth, count, mid, maxHalfHeight, centerGap);
            }
        }
    }

    private void RenderBarSet(
        DrawingContext context,
        IBrush brush,
        int barCount,
        double step,
        double barWidth,
        int count,
        double mid,
        double maxHalfHeight,
        double centerGap)
    {
        for (var b = 0; b < barCount; b++)
        {
            var x = b * step;
            var sampleIndex = Math.Clamp((int)((double)b / barCount * count), 0, count - 1);
            var amplitude = Math.Clamp(_waveform![sampleIndex], 0.04f, 1.0f);

            var halfBarH = amplitude * maxHalfHeight;
            var topY = mid - halfBarH - centerGap / 2.0;
            var bottomY = mid + halfBarH + centerGap / 2.0;
            var barH = bottomY - topY;

            context.FillRectangle(brush, new Rect(x, topY, barWidth, barH), 1.0f);
        }
    }

    private static void DrawFlatRail(
        DrawingContext context,
        double width,
        double height,
        IBrush playedBrush,
        IBrush remainingBrush,
        double progressX)
    {
        var railHeight = 4.0;
        var y = (height - railHeight) / 2.0;

        context.FillRectangle(remainingBrush, new Rect(0, y, width, railHeight), 2.0f);
        context.FillRectangle(playedBrush, new Rect(0, y, progressX, railHeight), 2.0f);
    }

    private void DrawTimestampBadge(DrawingContext context, double width, double handleX)
    {
        var timestampSec = Duration * Math.Clamp(handleX / width, 0, 1);
        var text = FormatTimestamp(timestampSec);
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium),
            10,
            Brushes.White);

        const double padX = 5.0;
        const double padY = 2.0;
        var badgeW = formattedText.Width + padX * 2;
        var badgeH = formattedText.Height + padY * 2;
        var badgeX = Math.Clamp(handleX - badgeW / 2.0, 2.0, Math.Max(2.0, width - badgeW - 2.0));
        var badgeY = -badgeH - 4.0;

        var badgeBg = new ImmutableSolidColorBrush(Color.Parse("#28282D"));
        var badgeBorder = new ImmutableSolidColorBrush(Color.Parse("#404048"));
        var badgePen = new Pen(badgeBorder, 1.0);

        context.FillRectangle(badgeBg, new Rect(badgeX, badgeY, badgeW, badgeH), 3.0f);
        context.DrawRectangle(null, badgePen, new Rect(badgeX, badgeY, badgeW, badgeH), 3.0f, 3.0f);
        context.DrawText(formattedText, new Point(badgeX + padX, badgeY + padY));
    }

    private static string FormatTimestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes}:{time.Seconds:D2}";
    }

    private static Color Lighten(Color c, double amount) => Color.FromArgb(
        c.A,
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));
}

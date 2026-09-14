using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Stramp.App.Controls;

/// <summary>
/// A compact saturation/value square with a hue strip underneath — drag either to pick a color.
/// Hue/sat/value are kept as the source of truth so dragging into black or grey doesn't lose the
/// hue you had selected (which is what happens if you round-trip through RGB every frame).
/// </summary>
public sealed class ColorPicker : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPicker, Color>(
            nameof(Color),
            Colors.MediumPurple,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private const double HueStripHeight = 14;
    private const double StripGap = 8;

    private double _hue;
    private double _saturation = 1;
    private double _value = 1;
    private bool _updatingFromColor;
    private bool _draggingSquare;
    private bool _draggingHue;

    static ColorPicker()
    {
        AffectsRender<ColorPicker>(ColorProperty);
    }

    public ColorPicker()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        SyncFromColor(Color);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorProperty && !_updatingFromColor)
            SyncFromColor(Color);
    }

    private void SyncFromColor(Color color)
    {
        var (h, s, v) = ToHsv(color);
        // Preserve the current hue when the color carries none (pure black/white/grey).
        if (s > 0.001)
            _hue = h;
        _saturation = s;
        _value = v;
    }

    private Rect SquareBounds => new(0, 0, Bounds.Width, Math.Max(0, Bounds.Height - HueStripHeight - StripGap));

    private Rect HueBounds => new(0, Math.Max(0, Bounds.Height - HueStripHeight), Bounds.Width, HueStripHeight);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var p = e.GetPosition(this);
        if (HueBounds.Contains(p))
            _draggingHue = true;
        else
            _draggingSquare = true;

        e.Pointer.Capture(this);
        Apply(p);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_draggingSquare || _draggingHue)
            Apply(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _draggingSquare = _draggingHue = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _draggingSquare = _draggingHue = false;
    }

    private void Apply(Point p)
    {
        if (_draggingHue)
        {
            var w = Math.Max(1, Bounds.Width);
            _hue = Math.Clamp(p.X / w, 0, 1) * 360;
        }
        else
        {
            var square = SquareBounds;
            if (square.Width <= 0 || square.Height <= 0)
                return;
            _saturation = Math.Clamp(p.X / square.Width, 0, 1);
            _value = 1 - Math.Clamp(p.Y / square.Height, 0, 1);
        }

        _updatingFromColor = true;
        Color = FromHsv(_hue, _saturation, _value);
        _updatingFromColor = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var square = SquareBounds;
        if (square.Width <= 0 || square.Height <= 0)
            return;

        // Saturation left-to-right over the pure hue, then value shaded to black downward.
        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(FromHsv(_hue, 1, 1), 1),
                },
            },
            square,
            6);

        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Colors.Black, 1),
                },
            },
            square,
            6);

        DrawThumb(context, new Point(
            square.X + _saturation * square.Width,
            square.Y + (1 - _value) * square.Height));

        var hue = HueBounds;
        var hueBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        for (var i = 0; i <= 6; i++)
            hueBrush.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));

        context.FillRectangle(hueBrush, hue, (float)(HueStripHeight / 2));
        DrawThumb(context, new Point(hue.X + _hue / 360 * hue.Width, hue.Center.Y), 6);
    }

    private static void DrawThumb(DrawingContext context, Point center, double radius = 7)
    {
        context.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(Colors.Black, 0.55), 3), center, radius, radius);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.6), center, radius, radius);
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        double h = 0;
        if (d > 1e-6)
        {
            if (Math.Abs(max - r) < 1e-6)
                h = (g - b) / d % 6;
            else if (Math.Abs(max - g) < 1e-6)
                h = (b - r) / d + 2;
            else
                h = (r - g) / d + 4;
            h *= 60;
            if (h < 0)
                h += 360;
        }

        return (h, max <= 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}

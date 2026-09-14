using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Stramp.App.Controls;

/// <summary>
/// Radial spectrum visualizer: bars radiate outward from a ring that frames the album art, low
/// frequencies at the top sweeping symmetrically down both sides to the highs. Each bar gets a
/// wide soft "glow" pass under a crisp core pass, with peak-hold dots that fall slowly, a bass-
/// reactive ring radius, and a faint full-width silhouette behind it so wide layouts don't feel
/// empty.
///
/// Levels arrive already dB-normalized (0-1); this control adds adaptive gain so quiet tracks
/// still fill the ring and loud ones don't pin at maximum.
/// </summary>
public sealed class SpectrumVisualizerControl : Control
{
    public static readonly StyledProperty<IBrush?> PrimaryBrushProperty =
        AvaloniaProperty.Register<SpectrumVisualizerControl, IBrush?>(nameof(PrimaryBrush));

    public static readonly StyledProperty<IBrush?> SecondaryBrushProperty =
        AvaloniaProperty.Register<SpectrumVisualizerControl, IBrush?>(nameof(SecondaryBrush));

    /// <summary>Radius of the ring the bars grow out of — set to just outside the album art.</summary>
    public static readonly StyledProperty<double> InnerRadiusProperty =
        AvaloniaProperty.Register<SpectrumVisualizerControl, double>(nameof(InnerRadius), 118d);

    public static readonly StyledProperty<bool> ShowRingProperty =
        AvaloniaProperty.Register<SpectrumVisualizerControl, bool>(nameof(ShowRing), true);

    public static readonly StyledProperty<bool> ShowSkylineProperty =
        AvaloniaProperty.Register<SpectrumVisualizerControl, bool>(nameof(ShowSkyline), true);

    public static readonly StyledProperty<double> SkylineOpacityProperty =
        AvaloniaProperty.Register<SpectrumVisualizerControl, double>(nameof(SkylineOpacity), 0.85d);

    private const float RiseRate = 0.55f;   // how fast a bar jumps up toward a louder value
    private const float FallRate = 0.12f;   // how fast it sinks back down
    private const float PeakFallRate = 0.006f;
    private const int HistoryDepth = 7;     // stacked translucent layers behind the live one
    private const int RingBars = 56;        // radial bars (downsampled from the full resolution)

    private float[] _levels = [];
    private float[] _peaks = [];
    private float[] _ringLevels = [];
    private float[] _ringPeaks = [];
    private readonly Queue<float[]> _history = new();
    private float _peakEnvelope = 0.35f;
    private float _gain = 1f;
    private float _bass;

    // Per-bin pens are rebuilt only when the palette/bin count/thickness changes, not every frame.
    private Pen[]? _corePens;
    private Pen[]? _glowPens;
    private IBrush[]? _peakBrushes;
    private int _cachedBinCount;
    private Color _cachedPrimary;
    private Color _cachedSecondary;
    private double _cachedThickness;

    static SpectrumVisualizerControl()
    {
        AffectsRender<SpectrumVisualizerControl>(
            PrimaryBrushProperty, SecondaryBrushProperty, InnerRadiusProperty,
            ShowRingProperty, ShowSkylineProperty, SkylineOpacityProperty);
    }

    public bool ShowRing
    {
        get => GetValue(ShowRingProperty);
        set => SetValue(ShowRingProperty, value);
    }

    public bool ShowSkyline
    {
        get => GetValue(ShowSkylineProperty);
        set => SetValue(ShowSkylineProperty, value);
    }

    public double SkylineOpacity
    {
        get => GetValue(SkylineOpacityProperty);
        set => SetValue(SkylineOpacityProperty, value);
    }

    public IBrush? PrimaryBrush
    {
        get => GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public IBrush? SecondaryBrush
    {
        get => GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public double InnerRadius
    {
        get => GetValue(InnerRadiusProperty);
        set => SetValue(InnerRadiusProperty, value);
    }

    /// <summary>Push a new analyzed frame (values 0-1). Safe at any rate; the control self-smooths.</summary>
    public void UpdateSpectrum(float[] bins)
    {
        if (_levels.Length != bins.Length)
        {
            _levels = new float[bins.Length];
            _peaks = new float[bins.Length];
            _history.Clear();
        }

        if (_ringLevels.Length == 0)
        {
            _ringLevels = new float[RingBars];
            _ringPeaks = new float[RingBars];
        }

        var frameMax = 0f;
        for (var i = 0; i < bins.Length; i++)
            frameMax = Math.Max(frameMax, bins[i]);

        // Adaptive gain: follow a slowly-decaying envelope of the loudest recent bin so the ring
        // fills out on quiet material without clipping on loud material.
        _peakEnvelope = Math.Max(frameMax, _peakEnvelope * 0.997f);
        var targetGain = _peakEnvelope > 0.08f ? 1f / _peakEnvelope : 1f;
        _gain += (Math.Clamp(targetGain, 1f, 3.2f) - _gain) * 0.04f;

        for (var i = 0; i < bins.Length; i++)
        {
            var target = Math.Clamp(bins[i] * _gain, 0f, 1f);
            var rate = target > _levels[i] ? RiseRate : FallRate;
            _levels[i] += (target - _levels[i]) * rate;

            _peaks[i] = _levels[i] >= _peaks[i]
                ? _levels[i]
                : Math.Max(_levels[i], _peaks[i] - PeakFallRate);
        }

        // Group the full-resolution bins down to the radial bar count.
        var perBar = (float)_levels.Length / RingBars;
        for (var j = 0; j < RingBars; j++)
        {
            var start = (int)(j * perBar);
            var end = Math.Max(start + 1, (int)((j + 1) * perBar));
            var sum = 0f;
            for (var i = start; i < end && i < _levels.Length; i++)
                sum += _levels[i];

            _ringLevels[j] = sum / (end - start);
            _ringPeaks[j] = _ringLevels[j] >= _ringPeaks[j]
                ? _ringLevels[j]
                : Math.Max(_ringLevels[j], _ringPeaks[j] - PeakFallRate);
        }

        _history.Enqueue((float[])_levels.Clone());
        while (_history.Count > HistoryDepth)
            _history.Dequeue();

        var bassBins = Math.Max(1, _levels.Length / 12);
        var bassSum = 0f;
        for (var i = 0; i < bassBins; i++)
            bassSum += _levels[i];
        _bass += (bassSum / bassBins - _bass) * 0.25f;

        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_levels);
        Array.Clear(_peaks);
        if (_ringLevels.Length > 0)
        {
            Array.Clear(_ringLevels);
            Array.Clear(_ringPeaks);
        }
        _history.Clear();
        _bass = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0 || _levels.Length < 4)
            return;

        var primary = (PrimaryBrush as ISolidColorBrush)?.Color ?? Color.Parse("#7C5CFF");
        var secondary = (SecondaryBrush as ISolidColorBrush)?.Color ?? Color.Parse("#FF5C93");

        if (ShowSkyline)
            DrawSilhouette(context, width, height, primary, secondary);
        if (ShowRing)
            DrawRadialBars(context, width, height, primary, secondary);
    }

    /// <summary>
    /// Full-width spectrum skyline behind the ring: several past frames stacked as translucent
    /// layers (oldest faintest) with hard stepped tops, so overlapping peaks build up a jagged,
    /// depth-y mountain range rather than one flat blob.
    /// </summary>
    private void DrawSilhouette(DrawingContext context, double width, double height, Color primary, Color secondary)
    {
        if (_history.Count == 0)
            return;

        // Tie vertical extent to width as well as height, so a narrow window gets a proportionally
        // shorter skyline instead of a squashed-looking wall of bars.
        var widthScale = Math.Clamp(width / 1100.0, 0.42, 1.0);
        var maxHeight = height * 0.52 * widthScale;
        var layers = _history.ToArray();
        var userOpacity = Math.Clamp(SkylineOpacity, 0, 1);

        for (var layerIndex = 0; layerIndex < layers.Length; layerIndex++)
        {
            // Newest layer last => drawn on top and most opaque.
            var age = (layerIndex + 1.0) / layers.Length;
            var opacity = (0.05 + 0.16 * age) * userOpacity;
            var scale = 0.72 + 0.28 * age; // older frames sit slightly lower, like a trail

            context.DrawGeometry(
                BuildSkylineBrush(primary, secondary, opacity),
                null,
                BuildSteppedSkyline(layers[layerIndex], width, height, maxHeight * scale));
        }

        DrawTraceLine(context, width, height, maxHeight, secondary, userOpacity);
    }

    /// <summary>Hard-stepped (bar-like) profile — the flat tops are what make it read as spiky.</summary>
    private static StreamGeometry BuildSteppedSkyline(float[] levels, double width, double height, double maxHeight)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        var n = levels.Length;
        var stepX = width / n;

        ctx.BeginFigure(new Point(0, height), isFilled: true);
        for (var i = 0; i < n; i++)
        {
            var x0 = i * stepX;
            var x1 = x0 + stepX;
            var y = height - Math.Clamp(levels[i], 0f, 1f) * maxHeight;
            ctx.LineTo(new Point(x0, y));
            ctx.LineTo(new Point(x1, y));
        }
        ctx.LineTo(new Point(width, height));
        ctx.EndFigure(isClosed: true);

        return geometry;
    }

    private static LinearGradientBrush BuildSkylineBrush(Color primary, Color secondary, double opacity) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        Opacity = opacity,
        GradientStops =
        {
            new GradientStop(Shift(primary, -0.28), 0),
            new GradientStop(primary, 0.28),
            new GradientStop(Lerp(primary, secondary, 0.5), 0.52),
            new GradientStop(secondary, 0.76),
            new GradientStop(Shift(secondary, 0.35), 1),
        },
    };

    /// <summary>A thin bright trace of the live spectrum low down, for a bit of crisp detail.</summary>
    private void DrawTraceLine(DrawingContext context, double width, double height, double maxHeight, Color color, double opacityScale)
    {
        var n = _levels.Length;
        if (n < 2)
            return;

        var pen = new Pen(new ImmutableSolidColorBrush(Lerp(color, Colors.White, 0.5), 0.35 * opacityScale), 1.2);
        var stepX = width / (n - 1);
        var traceHeight = maxHeight * 0.22;

        for (var i = 1; i < n; i++)
        {
            var p0 = new Point((i - 1) * stepX, height - _levels[i - 1] * traceHeight - 6);
            var p1 = new Point(i * stepX, height - _levels[i] * traceHeight - 6);
            context.DrawLine(pen, p0, p1);
        }
    }

    /// <summary>Darkens (negative) or lightens (positive) a color for gradient end stops.</summary>
    private static Color Shift(Color c, double amount) => amount < 0
        ? Color.FromArgb(c.A, (byte)(c.R * (1 + amount)), (byte)(c.G * (1 + amount)), (byte)(c.B * (1 + amount)))
        : Lerp(c, Colors.White, amount);

    private void DrawRadialBars(DrawingContext context, double width, double height, Color primary, Color secondary)
    {
        var center = new Point(width / 2, height / 2);
        var half = _ringLevels.Length / 2;
        if (half < 2)
            return;

        var baseRadius = InnerRadius + _bass * 12;
        // Never let bars reach the control edge, so nothing clips. Bar length is also tied to the
        // ring's own radius so a small window gets a proportionally compact ring, not stubby bars
        // on a tiny circle (or a ring that swallows the whole card).
        var available = Math.Min(width, height) / 2 - baseRadius - 8;
        var maxBarLength = Math.Clamp(available, 0, baseRadius * 0.78);
        if (maxBarLength <= 2)
            return;

        var thickness = Math.Max(2.5, 2 * Math.PI * baseRadius / _ringLevels.Length * 0.62);
        EnsurePens(half, primary, secondary, thickness);

        // Glow underlay first, then crisp cores on top.
        for (var pass = 0; pass < 2; pass++)
        {
            var pens = pass == 0 ? _glowPens! : _corePens!;

            for (var j = 0; j < half; j++)
            {
                var t = (double)j / (half - 1);
                var sweep = t * Math.PI; // top -> bottom
                var length = _ringLevels[j] * maxBarLength;
                if (length < 0.5)
                    continue;

                DrawBar(context, pens[j], center, -Math.PI / 2 + sweep, baseRadius, length);
                DrawBar(context, pens[j], center, -Math.PI / 2 - sweep, baseRadius, length);
            }
        }

        for (var j = 0; j < half; j++)
        {
            var peak = _ringPeaks[j];
            if (peak < 0.04f)
                continue;

            var t = (double)j / (half - 1);
            var sweep = t * Math.PI;
            var radius = baseRadius + peak * maxBarLength + 4;
            var brush = _peakBrushes![j];

            DrawPeakDot(context, brush, center, -Math.PI / 2 + sweep, radius);
            DrawPeakDot(context, brush, center, -Math.PI / 2 - sweep, radius);
        }
    }

    private static void DrawBar(DrawingContext context, Pen pen, Point center, double angle, double baseRadius, double length)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var p0 = new Point(center.X + cos * baseRadius, center.Y + sin * baseRadius);
        var p1 = new Point(center.X + cos * (baseRadius + length), center.Y + sin * (baseRadius + length));
        context.DrawLine(pen, p0, p1);
    }

    private static void DrawPeakDot(DrawingContext context, IBrush brush, Point center, double angle, double radius)
    {
        var p = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        context.DrawEllipse(brush, null, p, 1.6, 1.6);
    }

    private void EnsurePens(int count, Color primary, Color secondary, double thickness)
    {
        if (_corePens is not null
            && _cachedBinCount == count
            && _cachedPrimary == primary
            && _cachedSecondary == secondary
            && Math.Abs(_cachedThickness - thickness) < 0.01)
        {
            return;
        }

        _corePens = new Pen[count];
        _glowPens = new Pen[count];
        _peakBrushes = new IBrush[count];

        for (var i = 0; i < count; i++)
        {
            var t = count > 1 ? (double)i / (count - 1) : 0;
            var color = Lerp(primary, secondary, t);

            _corePens[i] = new Pen(
                new ImmutableSolidColorBrush(color, 0.95),
                thickness,
                lineCap: PenLineCap.Round);

            _glowPens[i] = new Pen(
                new ImmutableSolidColorBrush(color, 0.16),
                thickness * 3.2,
                lineCap: PenLineCap.Round);

            _peakBrushes[i] = new ImmutableSolidColorBrush(Lerp(color, Colors.White, 0.45), 0.85);
        }

        _cachedBinCount = count;
        _cachedPrimary = primary;
        _cachedSecondary = secondary;
        _cachedThickness = thickness;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}

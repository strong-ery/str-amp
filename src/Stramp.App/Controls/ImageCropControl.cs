using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Stramp.App.Controls;

/// <summary>
/// A square crop editor: drag to move the picture, scroll (or set <see cref="Zoom"/>) to zoom, and
/// <see cref="Rotation"/> turns it in quarter steps. Whatever is inside the bright square is what
/// <see cref="RenderCrop"/> produces; the rest of the picture shows dimmed around it. The picture
/// always covers the square, so there is never an empty edge in the result.
/// </summary>
public sealed class ImageCropControl : Control
{
    private const double Inset = 18;
    private const double MaxZoom = 4;

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageCropControl, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ImageCropControl, double>(
            nameof(Zoom), 1, coerce: (_, value) => Math.Clamp(value, 1, MaxZoom));

    /// <summary>Clockwise degrees, kept to 0, 90, 180 or 270.</summary>
    public static readonly StyledProperty<int> RotationProperty =
        AvaloniaProperty.Register<ImageCropControl, int>(
            nameof(Rotation), 0, coerce: (_, value) => (value % 360 + 360) % 360);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ImageCropControl>();

    /// <summary>Where the picture's centre sits relative to the square's centre, as a fraction of
    /// the square's side, so it holds its place when the control is resized.</summary>
    private Vector _offset;
    private Point? _dragStart;
    private Vector _dragStartOffset;

    static ImageCropControl()
    {
        AffectsRender<ImageCropControl>(SourceProperty, ZoomProperty, RotationProperty, BackgroundProperty);
        FocusableProperty.OverrideDefaultValue<ImageCropControl>(true);
        CursorProperty.OverrideDefaultValue<ImageCropControl>(new Cursor(StandardCursorType.SizeAll));
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public int Rotation { get => GetValue(RotationProperty); set => SetValue(RotationProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Back to the whole picture, centred and upright.</summary>
    public void Reset()
    {
        _offset = default;
        Zoom = 1;
        Rotation = 0;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A new picture starts whole and centred; anything else just has to stay in bounds.
        if (change.Property == SourceProperty)
        {
            _offset = default;
            Zoom = 1;
            Rotation = 0;
        }
        else if (change.Property == ZoomProperty || change.Property == RotationProperty)
        {
            _offset = Clamp(_offset, ViewportSide);
        }
    }

    private double ViewportSide => Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - 2 * Inset);

    private Rect Viewport
    {
        get
        {
            var side = ViewportSide;
            return new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
        }
    }

    /// <summary>Size of the picture after rotation, in its own units.</summary>
    private Size RotatedSize(Bitmap source) => Rotation % 180 == 0
        ? source.Size
        : new Size(source.Size.Height, source.Size.Width);

    /// <summary>Pixels of the square per unit of picture: just enough to cover it, times the zoom.</summary>
    private double ScaleFor(Bitmap source, double side)
    {
        var rotated = RotatedSize(source);
        return side / Math.Min(rotated.Width, rotated.Height) * Zoom;
    }

    /// <summary>Keeps the picture covering the whole square.</summary>
    private Vector Clamp(Vector offset, double side)
    {
        if (Source is not { } source)
            return default;

        var rotated = RotatedSize(source);
        var scale = ScaleFor(source, side);
        var limitX = Math.Max(0, (rotated.Width * scale - side) / 2) / side;
        var limitY = Math.Max(0, (rotated.Height * scale - side) / 2) / side;
        return new Vector(Math.Clamp(offset.X, -limitX, limitX), Math.Clamp(offset.Y, -limitY, limitY));
    }

    private void DrawPicture(DrawingContext context, Bitmap source, double side, Point centre)
    {
        var scale = ScaleFor(source, side);
        var offset = Clamp(_offset, side);
        var matrix =
            Matrix.CreateTranslation(-source.Size.Width / 2, -source.Size.Height / 2) *
            Matrix.CreateRotation(Rotation * Math.PI / 180) *
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(centre.X + offset.X * side, centre.Y + offset.Y * side);

        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        using (context.PushTransform(matrix))
            context.DrawImage(source, new Rect(source.Size));
    }

    /// <summary>The square inside the frame, as a finished bitmap of <paramref name="size"/> pixels.</summary>
    public RenderTargetBitmap? RenderCrop(int size)
    {
        if (Source is not { } source)
            return null;

        var target = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = target.CreateDrawingContext())
            DrawPicture(context, source, size, new Point(size / 2.0, size / 2.0));
        return target;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
            context.DrawRectangle(background, null, bounds);

        if (Source is not { } source)
            return;

        var viewport = Viewport;
        using (context.PushClip(bounds))
        {
            DrawPicture(context, source, viewport.Width, viewport.Center);

            // Everything outside the square is shown dimmed so it is clear what stays.
            var dim = new CombinedGeometry(
                GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(viewport));
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xB4, 0, 0, 0)), null, dim);
        }

        // Frame and rule-of-thirds guides.
        var frame = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)), 1.5);
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), 1);
        for (var line = 1; line <= 2; line++)
        {
            var x = viewport.X + viewport.Width * line / 3;
            var y = viewport.Y + viewport.Height * line / 3;
            context.DrawLine(guide, new Point(x, viewport.Y), new Point(x, viewport.Bottom));
            context.DrawLine(guide, new Point(viewport.X, y), new Point(viewport.Right, y));
        }
        context.DrawRectangle(null, frame, viewport);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragStart = e.GetPosition(this);
        _dragStartOffset = _offset;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start)
            return;

        var side = ViewportSide;
        var moved = e.GetPosition(this) - start;
        _offset = Clamp(_dragStartOffset + new Vector(moved.X / side, moved.Y / side), side);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null)
            return;

        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is null)
            return;

        Zoom *= Math.Pow(1.12, e.Delta.Y);
        e.Handled = true;
    }
}

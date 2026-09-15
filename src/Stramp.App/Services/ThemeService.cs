using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Stramp.Core.Settings;

namespace Stramp.App.Services;

/// <summary>
/// Pushes AppSettings' color choices into Application-level resources. Every themed brush in the
/// app is looked up via DynamicResource, so calling Apply() again (e.g. from the theme settings
/// window) re-themes the whole running UI instantly, no restart needed.
/// </summary>
public static class ThemeService
{
    private static Color? _currentPrimary;
    private static Color? _currentSecondary;
    private static Color? _currentBackground;
    private static DispatcherTimer? _animationTimer;

    /// <summary>Applies the manually-configured colors from settings.</summary>
    public static void Apply(AppSettings settings) => ApplyColors(
        ParseOrDefault(settings.PrimaryAccentColor, Color.Parse("#7C5CFF")),
        ParseOrDefault(settings.SecondaryAccentColor, Color.Parse("#FF5C93")),
        ParseOrDefault(settings.BackgroundColor, Color.Parse("#0E0E12")),
        animate: !settings.DisableAnimations);

    /// <summary>
    /// Applies colors pulled from album art. The user's manual picks in settings are left alone,
    /// so turning the "match album art" option back off restores exactly what they chose.
    /// </summary>
    public static void ApplyFromArt(ArtPalette palette, bool animate = true) => ApplyColors(
        palette.Primary,
        palette.Secondary,
        palette.Background,
        animate: animate);

    private static void ApplyColors(Color primary, Color secondary, Color background, bool animate = true)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyColors(primary, secondary, background, animate));
            return;
        }

        if (!_currentPrimary.HasValue || !_currentSecondary.HasValue || !_currentBackground.HasValue || !animate)
        {
            StopAnimation();
            UpdateCurrentColors(primary, secondary, background);
            ApplyColorsDirect(primary, secondary, background);
            return;
        }

        var startPrimary = _currentPrimary.Value;
        var startSecondary = _currentSecondary.Value;
        var startBackground = _currentBackground.Value;

        if (startPrimary == primary && startSecondary == secondary && startBackground == background)
        {
            return;
        }

        StopAnimation();

        var startTime = DateTime.UtcNow;
        const double durationMs = 350.0;

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _animationTimer.Tick += (s, e) =>
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            double t = Math.Clamp(elapsed / durationMs, 0.0, 1.0);

            double easedT = t * t * (3 - 2 * t);

            var currP = LerpColor(startPrimary, primary, easedT);
            var currS = LerpColor(startSecondary, secondary, easedT);
            var currB = LerpColor(startBackground, background, easedT);

            UpdateCurrentColors(currP, currS, currB);
            ApplyColorsDirect(currP, currS, currB);

            if (t >= 1.0)
            {
                StopAnimation();
            }
        };

        _animationTimer.Start();
    }

    private static void StopAnimation()
    {
        if (_animationTimer is not null)
        {
            _animationTimer.Stop();
            _animationTimer = null;
        }
    }

    private static void UpdateCurrentColors(Color primary, Color secondary, Color background)
    {
        _currentPrimary = primary;
        _currentSecondary = secondary;
        _currentBackground = background;
    }

    private static Color LerpColor(Color c1, Color c2, double t)
    {
        byte a = (byte)(c1.A + (c2.A - c1.A) * t);
        byte r = (byte)(c1.R + (c2.R - c1.R) * t);
        byte g = (byte)(c1.G + (c2.G - c1.G) * t);
        byte b = (byte)(c1.B + (c2.B - c1.B) * t);
        return Color.FromArgb(a, r, g, b);
    }

    private static void ApplyColorsDirect(Color primary, Color secondary, Color background)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.Resources["AppAccentBrush"] = new SolidColorBrush(primary);
        app.Resources["AppAccentBrushLight"] = new SolidColorBrush(Lighten(primary, 0.18));
        app.Resources["AppPlayPauseIconBrush"] = new SolidColorBrush(
            RelativeLuminance(primary) >= 0.50 ? Colors.Black : Colors.White);
        app.Resources["AppSecondaryAccentBrush"] = new SolidColorBrush(secondary);
        app.Resources["AppBackgroundBrush"] = new SolidColorBrush(background);
        app.Resources["AppSubtleBackgroundBrush"] = new SolidColorBrush(WithAlpha(primary, 0x20));
        app.Resources["AppPlaceholderBackgroundBrush"] = new SolidColorBrush(WithAlpha(primary, 0x14));
        app.Resources["AppHoverBrush"] = new SolidColorBrush(WithAlpha(primary, 0x16));
        app.Resources["AppDialogBackgroundBrush"] = new SolidColorBrush(Lighten(background, 0.07));

        ApplyBuiltInControlAccents(app, primary);
    }

    /// <summary>
    /// FluentTheme paints sliders, focus rings and selection with the OS accent color (which is
    /// why an untouched build shows the user's Windows accent — red, here). Overriding the system
    /// accent palette plus the slider-specific brushes keeps built-in controls on our theme.
    /// </summary>
    private static void ApplyBuiltInControlAccents(Application app, Color primary)
    {
        app.Resources["SystemAccentColor"] = primary;
        app.Resources["SystemAccentColorLight1"] = Lighten(primary, 0.2);
        app.Resources["SystemAccentColorLight2"] = Lighten(primary, 0.4);
        app.Resources["SystemAccentColorLight3"] = Lighten(primary, 0.6);
        app.Resources["SystemAccentColorDark1"] = Darken(primary, 0.2);
        app.Resources["SystemAccentColorDark2"] = Darken(primary, 0.4);
        app.Resources["SystemAccentColorDark3"] = Darken(primary, 0.6);

        var accent = new SolidColorBrush(primary);
        var accentLight = new SolidColorBrush(Lighten(primary, 0.2));
        var accentDark = new SolidColorBrush(Darken(primary, 0.15));

        app.Resources["SliderTrackValueFill"] = accent;
        app.Resources["SliderTrackValueFillPointerOver"] = accentLight;
        app.Resources["SliderTrackValueFillPressed"] = accentDark;
        app.Resources["SliderThumbBackground"] = accent;
        app.Resources["SliderThumbBackgroundPointerOver"] = accentLight;
        app.Resources["SliderThumbBackgroundPressed"] = accentDark;
    }

    private static Color ParseOrDefault(string value, Color fallback)
    {
        try
        {
            return Color.Parse(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static Color Lighten(Color c, double amount) => Color.FromArgb(
        c.A,
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromArgb(
        c.A,
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) +
               0.7152 * Linearize(color.G) +
               0.0722 * Linearize(color.B);
    }
}

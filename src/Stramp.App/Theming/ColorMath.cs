using Avalonia.Media;

namespace Stramp.App.Theming;

/// <summary>Color-space conversions and perceptual adjustments shared by the album palette and the theme.</summary>
/// <remarks>
/// HSL hue is in degrees (0-360); saturation, lightness and luminance are 0-1. Luminance is WCAG
/// relative luminance, so blue reads darker than yellow at the same channel values. The luminance
/// bounds move only HSL lightness, keeping hue and saturation, and change a color no further than
/// needed to reach the bound. Alpha is preserved everywhere.
/// </remarks>
public static class ColorMath
{
    public static double RelativeLuminance(Color color)
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

    public static Color WithMinimumLuminance(Color color, double minimum) =>
        RelativeLuminance(color) >= minimum ? color : SearchLightness(color, minimum, raise: true);

    public static Color WithMaximumLuminance(Color color, double maximum) =>
        RelativeLuminance(color) <= maximum ? color : SearchLightness(color, maximum, raise: false);

    // Binary search over lightness between the color's own and the extreme in the needed direction,
    // ending on the side of the bound that satisfies it.
    private static Color SearchLightness(Color color, double target, bool raise)
    {
        var (hue, saturation, lightness) = ToHsl(color.R / 255.0, color.G / 255.0, color.B / 255.0);
        var satisfied = raise ? 1.0 : 0.0;
        var unsatisfied = lightness;

        for (var iteration = 0; iteration < 12; iteration++)
        {
            var candidate = (satisfied + unsatisfied) / 2;
            var luminance = RelativeLuminance(FromHsl(hue, saturation, candidate));
            if (raise ? luminance >= target : luminance <= target)
                satisfied = candidate;
            else
                unsatisfied = candidate;
        }

        var moved = FromHsl(hue, saturation, satisfied);
        return Color.FromArgb(color.A, moved.R, moved.G, moved.B);
    }

    public static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;

        if (Math.Abs(max - min) < 1e-6)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (Math.Abs(max - r) < 1e-6)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 1e-6)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;

        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;

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

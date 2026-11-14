using Avalonia.Media;
using Stramp.Core.Settings;

namespace Stramp.App.Theming;

/// <summary>The three colors the user picks by hand in the theme panel, as stored in settings.</summary>
/// <remarks>
/// <see cref="Default"/> is the palette a fresh install starts with, taken from a new
/// <see cref="AppSettings"/> so the shipped defaults are written down once. <see cref="From"/> reads a
/// settings object and falls back to the default for each color that does not parse, so a
/// hand-edited settings file never breaks theming.
/// </remarks>
public readonly record struct ManualPalette(Color Primary, Color Secondary, Color Background)
{
    private static readonly AppSettings Shipped = new();

    public static ManualPalette Default { get; } = new(
        Color.Parse(Shipped.PrimaryAccentColor),
        Color.Parse(Shipped.SecondaryAccentColor),
        Color.Parse(Shipped.BackgroundColor));

    public static ManualPalette From(AppSettings settings) => new(
        Parse(settings.PrimaryAccentColor, Default.Primary),
        Parse(settings.SecondaryAccentColor, Default.Secondary),
        Parse(settings.BackgroundColor, Default.Background));

    private static Color Parse(string? value, Color fallback) =>
        value is not null && Color.TryParse(value, out var color) ? color : fallback;
}

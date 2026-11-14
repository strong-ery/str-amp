using Avalonia.Media;
using Avalonia.Styling;

namespace Stramp.App.Theming;

/// <summary>The visual themes Stramp ships with.</summary>
/// <remarks>
/// <see cref="All"/> is the order the settings picker lists them in. <see cref="Find"/> resolves an
/// id read from settings and falls back to <see cref="Dark"/> for an id this build does not know,
/// so a settings file written by another build always opens.
/// </remarks>
public static class VisualThemes
{
    public static VisualTheme Dark { get; } = new("dark", "Dark", ThemeVariant.Dark);

    public static VisualTheme Light { get; } = new("light", "Light", ThemeVariant.Light);

    // Glass needs a see-through background; the window's blur hint lives in the style sheet.
    public static VisualTheme Aero { get; } = new(
        "aero",
        "Aero",
        ThemeVariant.Dark,
        StyleSheet: new Uri("avares://Stramp.App/Styles/Themes/Aero.axaml"),
        Background: Color.Parse("#C0102B3E"),
        SliderAccent: Color.Parse("#28B83A"));

    public static IReadOnlyList<VisualTheme> All { get; } = [Dark, Light, Aero];

    public static VisualTheme Find(string? id) =>
        All.FirstOrDefault(theme => theme.Id == id) ?? Dark;
}

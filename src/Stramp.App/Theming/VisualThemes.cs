using Avalonia.Media;

namespace Stramp.App.Theming;

/// <summary>The visual themes Stramp ships with.</summary>
/// <remarks>
/// <see cref="All"/> is the order the settings picker lists them in. <see cref="Find"/> resolves an
/// id read from settings and falls back to <see cref="Default"/> for an id this build does not know,
/// so a settings file written by another build always opens.
/// </remarks>
public static class VisualThemes
{
    public static VisualTheme Default { get; } = new("default", "Default");

    // Glass needs a see-through background; the window's blur hint lives in the style sheet.
    public static VisualTheme Aero { get; } = new(
        "aero",
        "Aero",
        StyleSheet: new Uri("avares://Stramp.App/Styles/Themes/Aero.axaml"),
        Background: Color.Parse("#C0102B3E"),
        SliderAccent: Color.Parse("#28B83A"));

    public static IReadOnlyList<VisualTheme> All { get; } = [Default, Aero];

    public static VisualTheme Find(string? id) =>
        All.FirstOrDefault(theme => theme.Id == id) ?? Default;
}

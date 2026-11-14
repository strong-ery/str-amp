using Avalonia.Media;

namespace Stramp.App.Theming;

/// <summary>
/// A named look for the whole window: a style sheet layered over the base styles and the colors it
/// pins regardless of the active palette.
/// </summary>
/// <remarks>
/// A theme never replaces the palette. Accents still come from the manual picks or the album art;
/// the theme only overrides what it names. <see cref="StyleSheet"/> is null for a theme that uses the
/// base styles alone; a sheet stays loaded for the life of the app and scopes every selector under
/// <see cref="StyleClass"/>, which the main window carries only while the theme is active.
/// <see cref="Background"/> replaces the palette's background when set, and <see cref="SliderAccent"/>
/// replaces the accent on the built-in slider brushes when set. <see cref="Id"/> is what settings
/// store, so it does not change once a theme has shipped.
/// </remarks>
public sealed record VisualTheme(
    string Id,
    string Name,
    Uri? StyleSheet = null,
    Color? Background = null,
    Color? SliderAccent = null)
{
    public string StyleClass => "theme-" + Id;
}

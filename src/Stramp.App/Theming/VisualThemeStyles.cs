using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace Stramp.App.Theming;

/// <summary>Switches the main window between visual themes by its theme class.</summary>
/// <remarks>
/// Every theme's style sheet is added to the application's styles on first use and never removed;
/// switching only swaps the window's <see cref="VisualTheme.StyleClass"/>. Selectors react to a class
/// change everywhere, including inside popups that are closed at the time. The window is registered
/// once through <see cref="Host"/>, and a theme activated before that is applied when it arrives.
/// Call both on the UI thread.
/// </remarks>
public static class VisualThemeStyles
{
    private static VisualTheme _active = VisualThemes.Default;
    private static Window? _host;
    private static bool _sheetsLoaded;

    public static void Activate(Application app, VisualTheme theme)
    {
        LoadSheets(app);
        if (ReferenceEquals(theme, _active))
            return;

        _host?.Classes.Remove(_active.StyleClass);
        _active = theme;
        _host?.Classes.Add(_active.StyleClass);
    }

    public static void Host(Window window)
    {
        _host = window;
        window.Classes.Add(_active.StyleClass);
    }

    private static void LoadSheets(Application app)
    {
        if (_sheetsLoaded)
            return;

        foreach (var theme in VisualThemes.All)
        {
            if (theme.StyleSheet is { } source)
                app.Styles.Add(new StyleInclude(source) { Source = source });
        }

        _sheetsLoaded = true;
    }
}

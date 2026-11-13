using Avalonia.Media;

namespace Stramp.App.ViewModels;

/// <summary>A built-in color combination offered in the theme panel's preset picker.</summary>
public sealed record ThemePreset(string Name, string Primary, string Secondary, string Background)
{
    public IBrush PrimaryBrush => new SolidColorBrush(Color.Parse(Primary));
    public IBrush SecondaryBrush => new SolidColorBrush(Color.Parse(Secondary));
}

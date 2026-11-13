using System.Globalization;
using Avalonia.Data.Converters;

namespace Stramp.App.Converters;

/// <summary>True to fully opaque, false to fully transparent - paired with an Opacity transition
/// so toggling the bound bool fades the element out rather than popping it away.</summary>
public sealed class BoolToFadeOpacityConverter : IValueConverter
{
    public static readonly BoolToFadeOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

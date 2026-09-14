using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia;

namespace Stramp.App.Converters;

/// <summary>Collapses the gap next to the library panel along with its width, so closed really means closed.</summary>
public sealed class BoolToLibraryMarginConverter : IValueConverter
{
    public static readonly BoolToLibraryMarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(0, 0, 10, 0) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

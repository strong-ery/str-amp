using System.Globalization;
using Avalonia.Data.Converters;

namespace Stramp.App.Converters;

/// <summary>Drives the animated width of the collapsible library panel.</summary>
public sealed class BoolToLibraryWidthConverter : IValueConverter
{
    public static readonly BoolToLibraryWidthConverter Instance = new();
    public const double OpenWidth = 280;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? OpenWidth : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
using Avalonia.Data.Converters;

namespace Stramp.App.Converters;

/// <summary>True when the bound value is non-null. Used to swap an art Image for a placeholder icon.</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public static readonly NullToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNonNull = value is not null;
        var invert = "invert".Equals(parameter as string, StringComparison.OrdinalIgnoreCase);
        return invert ? !isNonNull : isNonNull;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
using Avalonia.Data.Converters;

namespace Stramp.App.Converters;

/// <summary>True when the bound enum value's name matches the converter parameter string. Used
/// to switch a panel's visibility by step/case without a bool property per value.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

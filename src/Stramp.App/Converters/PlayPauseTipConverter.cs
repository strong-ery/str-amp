using System.Globalization;
using Avalonia.Data.Converters;

namespace Stramp.App.Converters;

/// <summary>Tooltip for a source's Play button: "Pause" while that source is playing, else "Play".</summary>
public sealed class PlayPauseTipConverter : IValueConverter
{
    public static readonly PlayPauseTipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Pause" : "Play";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

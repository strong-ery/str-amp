using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Stramp.App.Converters;

public sealed class PlayPauseIconConverter : IValueConverter
{
    public static readonly PlayPauseIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Icons.Pause : Icons.Play;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
using Avalonia.Data.Converters;

namespace NoteScribe.App.Converters;

/// <summary>Scales a 0..1 fraction onto a fixed pixel track — used for the peak-hold marker.</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0, 1) : 0;
        var track = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            ? t
            : 100.0;

        return fraction * track;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

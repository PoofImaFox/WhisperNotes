using System.Globalization;
using Avalonia.Data.Converters;

namespace NoteScribe.App.Converters;

/// <summary>Dims rather than hides, so meter LEDs and hover affordances keep their layout slot.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public double TrueOpacity { get; set; } = 1.0;

    public double FalseOpacity { get; set; } = 0.14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueOpacity : FalseOpacity;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

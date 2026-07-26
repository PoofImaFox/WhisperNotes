using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace WhisperNotes.App.Views;

public partial class NotesBrowserView : UserControl
{
    public NotesBrowserView() => InitializeComponent();
}

/// <summary>
/// Project and date rows in the saved-meetings tree are group headers, not content. Casing them
/// up leaves the meeting titles as the only mixed-case text in the column, so the eye lands on
/// the meetings rather than on the folders holding them.
/// </summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(culture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value;
}

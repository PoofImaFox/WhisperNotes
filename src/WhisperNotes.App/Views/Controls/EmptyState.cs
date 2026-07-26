using Avalonia;
using Avalonia.Controls.Primitives;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// "There is nothing here, and here is what to do about it."
/// <para>
/// Four views spell this out longhand and none of them agree: two have a heading and two are a
/// bare sentence, the two with headings disagree on <c>MaxWidth</c>, and one wraps itself in card
/// chrome while the other does not. Supply <see cref="Heading"/> or leave it null; the shape is
/// the same either way.
/// </para>
/// <para>
/// <c>IsHitTestVisible</c> defaults to false in the theme, because one of the four floats over a
/// live ListBox and had to turn it off by hand — and an empty state is never interactive
/// anywhere else either, so that is the right default rather than a special case.
/// </para>
/// </summary>
public sealed class EmptyState : TemplatedControl
{
    /// <summary>Optional first line, in the <c>h2</c> class. Collapses when null or empty.</summary>
    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Heading));

    /// <summary>The sentence that says what to do next. Wrapped, centred, never trimmed.</summary>
    public static readonly StyledProperty<string?> BodyProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Body));

    /// <summary>Gap between heading and body. Theme default is <c>Space4</c>.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<EmptyState, double>(nameof(Spacing), 12d);

    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
}

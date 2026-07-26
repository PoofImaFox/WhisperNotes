using Avalonia;
using Avalonia.Controls;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// The rule-and-summary strip that closes a pane: a caption on the left, actions on the right,
/// and anything else stacked underneath.
/// <para>
/// Both existing footers are a Border with a <c>0,1,0,0</c> hairline, <c>PadCard</c> and a
/// <c>*,Auto</c> Grid with a caption in column 0. They differ only in what hangs off the right
/// and, for the notes library, in the export block sitting below the summary row — hence the
/// split between <see cref="Actions"/> (beside the summary) and <c>Content</c> (below it) rather
/// than one content slot that only fits the simpler of the two.
/// </para>
/// </summary>
public sealed class PaneFooter : ContentControl
{
    /// <summary>Left-hand caption — a count, a path, a state.</summary>
    public static readonly StyledProperty<string?> SummaryProperty =
        AvaloniaProperty.Register<PaneFooter, string?>(nameof(Summary));

    /// <summary>Right-hand strip on the summary row. Usually a <c>StackPanel Classes="actions"</c>.</summary>
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PaneFooter, object?>(nameof(Actions));

    /// <summary>Gap between the summary row and whatever <c>Content</c> stacks beneath it.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PaneFooter, double>(nameof(Spacing), 4d);

    public string? Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
}

using Avalonia;
using Avalonia.Controls;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// A pane's title row: a section label on the left, an action strip on the right.
/// <para>
/// <c>NotesBrowserView</c> and <c>NotesWorkspaceView</c> both open with this exact block — same
/// title, same two button labels, same off-scale <c>Margin="12,12,10,8"</c>. The only reason
/// they were not literally copy-pasted is that they point at different commands. The margin is
/// now <c>PadPaneHeader</c>, squared off to <c>12,12,12,8</c> so the right gutter matches the
/// left one.
/// </para>
/// </summary>
public sealed class PaneHeader : ContentControl
{
    /// <summary>Section title. Rendered with the <c>label</c> text class.</summary>
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<PaneHeader, string?>(nameof(Header));

    /// <summary>Gap between the action buttons the caller puts in <c>Content</c>.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PaneHeader, double>(nameof(Spacing), 4d);

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
}

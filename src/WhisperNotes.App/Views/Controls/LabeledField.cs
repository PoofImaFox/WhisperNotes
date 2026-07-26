using Avalonia;
using Avalonia.Controls;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// A caption above an arbitrary editor, with optional help text underneath.
/// <para>
/// Nine settings fields wrote this out by hand: six in <c>AiSettingsView</c>, two in
/// <c>InputSettingsView</c>, one in <c>NotesWorkspaceView</c>. The content differs every time —
/// a TextBox, a ComboBox, a Grid of box-plus-button — which is why this is a
/// <see cref="ContentControl"/> and not a UserControl with a fixed inner editor.
/// </para>
/// <para>
/// It also owns the gap <em>between</em> consecutive fields, which two of the AiSettings fields
/// were faking with a <c>Margin="0,4,0,0"</c> on their caption, and it makes the "Max output
/// tokens" field — the one that was laid out horizontally while its eight siblings were vertical
/// — agree with the rest for free.
/// </para>
/// <para>
/// <see cref="Header"/> is a string rather than the <c>object</c> a HeaderedContentControl would
/// give: every call site passes a plain caption, and a string can be bound straight to a
/// TextBlock without a second content presenter and a template to go with it.
/// </para>
/// </summary>
public sealed class LabeledField : ContentControl
{
    /// <summary>Caption above the editor. Rendered with the <c>caption</c> text class.</summary>
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<LabeledField, string?>(nameof(Header));

    /// <summary>
    /// Optional wrapped note below the editor. Collapses when null or empty, so a field that has
    /// nothing to explain costs no vertical space.
    /// </summary>
    public static readonly StyledProperty<string?> HelpTextProperty =
        AvaloniaProperty.Register<LabeledField, string?>(nameof(HelpText));

    /// <summary>Gap between caption, editor and help text. Theme default is <c>Space2</c>.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<LabeledField, double>(nameof(Spacing), 4d);

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? HelpText
    {
        get => GetValue(HelpTextProperty);
        set => SetValue(HelpTextProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
}

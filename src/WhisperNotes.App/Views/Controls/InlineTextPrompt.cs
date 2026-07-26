using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;

namespace WhisperNotes.App.Views.Controls;

/// <summary>
/// A one-field prompt that appears in place, instead of a modal dialog: optional caption, a text
/// box, and a confirm/cancel pair, with Enter and Escape wired to the same two commands.
/// <para>
/// Three sites build this by hand and disagree on all three axes — commit labels "Rename",
/// "Rename speaker" and "Rename", one has a <c>MaxWidth</c> and the others do not, one has a
/// caption above and the others do not.
/// </para>
/// <para>
/// The commands are <see cref="StyledProperty{T}"/> rather than an inherited DataContext on
/// purpose: the three call sites bind against three different view models, and a control that
/// reached into whatever DataContext it happened to land in would work by luck. Bind them from
/// outside and the compiled-binding checker verifies each site against its own view model.
/// </para>
/// <para>
/// There is no <c>IsOpen</c>. Every site already gates the block with <c>IsVisible</c> and that
/// is one honest attribute; a second visibility switch on the same control would just be a way
/// for the two to disagree.
/// </para>
/// </summary>
public sealed class InlineTextPrompt : TemplatedControl
{
    /// <summary>The edited text. Two-way by default — this is a field, not a readout.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InlineTextPrompt, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Optional caption above the field.</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<InlineTextPrompt, string?>(nameof(Label));

    /// <summary>Ghost text inside the empty field.</summary>
    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<InlineTextPrompt, string?>(nameof(Placeholder));

    /// <summary>Screen-reader name for the field, which is rarely the same as the caption.</summary>
    public static readonly StyledProperty<string?> AccessibleNameProperty =
        AvaloniaProperty.Register<InlineTextPrompt, string?>(nameof(AccessibleName));

    /// <summary>Runs on the accent button and on Enter.</summary>
    public static readonly StyledProperty<ICommand?> CommitCommandProperty =
        AvaloniaProperty.Register<InlineTextPrompt, ICommand?>(nameof(CommitCommand));

    /// <summary>Runs on the subtle button and on Escape.</summary>
    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<InlineTextPrompt, ICommand?>(nameof(CancelCommand));

    public static readonly StyledProperty<string> CommitLabelProperty =
        AvaloniaProperty.Register<InlineTextPrompt, string>(nameof(CommitLabel), "Rename");

    public static readonly StyledProperty<string> CancelLabelProperty =
        AvaloniaProperty.Register<InlineTextPrompt, string>(nameof(CancelLabel), "Cancel");

    /// <summary>Gap between caption, field and button row. Theme default is <c>Space2</c>.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<InlineTextPrompt, double>(nameof(Spacing), 4d);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public string? AccessibleName
    {
        get => GetValue(AccessibleNameProperty);
        set => SetValue(AccessibleNameProperty, value);
    }

    public ICommand? CommitCommand
    {
        get => GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public string CommitLabel
    {
        get => GetValue(CommitLabelProperty);
        set => SetValue(CommitLabelProperty, value);
    }

    public string CancelLabel
    {
        get => GetValue(CancelLabelProperty);
        set => SetValue(CancelLabelProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>
    /// Enter commits, Escape cancels. Handled here rather than with KeyBindings inside the control
    /// template: items in a <c>KeyBindings</c> collection are not part of the template's visual
    /// tree, so <c>{TemplateBinding}</c> never reaches them and the command would silently be
    /// null. The inner TextBox has <c>AcceptsReturn="False"</c>, so both keys bubble up to here
    /// unhandled.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        var command = e.Key switch
        {
            Key.Enter => CommitCommand,
            Key.Escape => CancelCommand,
            _ => null,
        };

        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
        e.Handled = true;
    }
}

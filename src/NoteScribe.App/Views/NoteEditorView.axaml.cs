using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using NoteScribe.App.ViewModels;
using TextMateSharp.Grammars;

namespace NoteScribe.App.Views;

/// <summary>
/// Hosts the AvaloniaEdit surface and keeps it in step with <see cref="NoteEditorViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the text is not bound.</b> <c>TextEditor.Text</c> is a plain CLR property, not a styled
/// one; a two-way binding against it drops changes and fights the undo stack. The control is
/// therefore driven from here: the VM raises <see cref="NoteEditorViewModel.TextReplaced"/> when it
/// swaps the whole body (open, revert, apply an AI result) and <c>TextChanged</c> reports typing
/// back. <see cref="_suppress"/> breaks the loop between the two.
/// </para>
/// <para>
/// Replacing <c>Document.Text</c> rather than assigning a fresh <see cref="TextDocument"/> is
/// deliberate: a new document would throw away AvaloniaEdit's undo stack, and Ctrl+Z after an AI
/// apply is exactly when a user wants it most.
/// </para>
/// </remarks>
public partial class NoteEditorView : UserControl
{
    private NoteEditorViewModel? _model;
    private TextMate.Installation? _textMate;
    private bool _suppress;

    public NoteEditorView()
    {
        InitializeComponent();

        Editor.Document = new TextDocument(string.Empty);
        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.SelectionChanged += OnSelectionChanged;

        InstallSyntaxHighlighting();
        ApplyEditorPalette();

        DataContextChanged += OnDataContextChanged;

        // Re-wiring on attach keeps the view usable if the shell ever removes and re-adds the
        // page rather than just toggling IsVisible on it.
        AttachedToVisualTree += (_, _) => Wire();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    /// <summary>Markdown grammar + Dark+ theme, so headings, code fences and links read at a glance.</summary>
    private void InstallSyntaxHighlighting()
    {
        try
        {
            var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
            _textMate = Editor.InstallTextMate(registryOptions);
            _textMate.SetGrammar(registryOptions.GetScopeByLanguageId(
                registryOptions.GetLanguageByExtension(".md").Id));
        }
        catch (Exception ex)
        {
            // Highlighting is a nicety; a missing grammar bundle must not cost the user their editor.
            System.Diagnostics.Trace.TraceWarning($"TextMate unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-imposes the app palette over whatever the TextMate theme just painted, so the editor
    /// matches the surrounding panes instead of Dark+'s own charcoal.
    /// </summary>
    private void ApplyEditorPalette()
    {
        Editor.Background = Token("EditorBackground") ?? Editor.Background;
        Editor.Foreground = Token("TextPrimary") ?? Editor.Foreground;
        Editor.LineNumbersForeground = Token("EditorLineNumber") ?? Editor.LineNumbersForeground;

        var area = Editor.TextArea;
        area.Background = Token("EditorBackground") ?? area.Background;
        area.SelectionBrush = Token("EditorSelection") ?? area.SelectionBrush;
        area.SelectionCornerRadius = 2;
        area.SelectionBorder = null;

        if (Token("EditorCaret") is { } caret)
        {
            area.Caret.CaretBrush = caret;
        }

        if (Token("EditorCurrentLine") is { } current)
        {
            area.TextView.CurrentLineBackground = current;
            area.TextView.CurrentLineBorder = new Pen(current, 0);
        }
    }

    private IBrush? Token(string key)
    {
        if (this.TryFindResource(key, out var local) && local is IBrush brush)
        {
            return brush;
        }

        return Application.Current is { } app && app.TryFindResource(key, out var global)
            ? global as IBrush
            : null;
    }

    // ---- view-model wiring -------------------------------------------------------------------

    private void OnDataContextChanged(object? sender, EventArgs e) => Wire();

    private void Wire()
    {
        Detach();

        if (DataContext is not NoteEditorViewModel model)
        {
            return;
        }

        _model = model;
        model.TextReplaced += OnTextReplaced;
        model.CopyRequested += OnCopyRequested;
        model.PropertyChanged += OnModelPropertyChanged;

        SetText(model.Content);
        ApplyPaneWidths(model.PaneMode);
    }

    private void Detach()
    {
        if (_model is null)
        {
            return;
        }

        _model.TextReplaced -= OnTextReplaced;
        _model.CopyRequested -= OnCopyRequested;
        _model.PropertyChanged -= OnModelPropertyChanged;
        _model = null;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteEditorViewModel.PaneMode) && _model is { } model)
        {
            ApplyPaneWidths(model.PaneMode);
        }
    }

    private void OnTextReplaced(object? sender, string text) => SetText(text);

    private void SetText(string text)
    {
        if (string.Equals(Editor.Document.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        var caret = Editor.CaretOffset;

        _suppress = true;
        try
        {
            Editor.Document.Text = text;
        }
        finally
        {
            _suppress = false;
        }

        Editor.CaretOffset = Math.Clamp(caret, 0, Editor.Document.TextLength);
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        _model?.OnEditorTextChanged(Editor.Document.Text);
    }

    private void OnSelectionChanged(object? sender, EventArgs e) =>
        _model?.SetSelection(Editor.SelectionStart, Editor.SelectionLength);

    private async void OnCopyRequested(object? sender, string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Clipboard unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// A hidden child still leaves its star-sized column holding a share of the width, so the split
    /// is driven by the column definitions rather than by <c>IsVisible</c> alone.
    /// </summary>
    private void ApplyPaneWidths(NotePaneMode mode)
    {
        var editor = mode is NotePaneMode.Editor or NotePaneMode.Split;
        var preview = mode is NotePaneMode.Preview or NotePaneMode.Split;

        PaneGrid.ColumnDefinitions[0].Width = editor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PaneGrid.ColumnDefinitions[2].Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }
}

using Avalonia.Controls;

namespace WhisperNotes.App.Views;

/// <summary>
/// Revision history and the unified diff between a chosen snapshot and the text on screen.
/// Bound to the same <see cref="ViewModels.NoteEditorViewModel"/> as the editor, so "restore"
/// lands in the open document rather than in a detached copy of it.
/// </summary>
public partial class NoteRevisionsView : UserControl
{
    public NoteRevisionsView()
    {
        InitializeComponent();
    }
}

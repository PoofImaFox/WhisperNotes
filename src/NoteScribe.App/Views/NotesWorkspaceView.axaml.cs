using Avalonia.Controls;

namespace NoteScribe.App.Views;

/// <summary>
/// The Notes page: library, editor and the assistant/history rail. The shell hosts exactly this
/// control, bound to a <see cref="ViewModels.NotesWorkspaceViewModel"/>.
/// </summary>
public partial class NotesWorkspaceView : UserControl
{
    public NotesWorkspaceView()
    {
        InitializeComponent();
    }
}

using WhisperNotes.App.ViewModels;

namespace WhisperNotes.App.Shell;

/// <summary>
/// The slice of the shell a recording drives: the transport flags the toolbar binds to, the
/// status line, and the page the user is dropped on when a pre-flight check fails.
/// </summary>
/// <remarks>
/// Deliberately a strict subset of members <see cref="MainWindowViewModel"/> already exposes, so
/// the shell satisfies it without growing a single member and the binding surface is untouched.
/// The point is the other direction: a collaborator gets a named, four-member contract instead of
/// a reference to the whole view model.
/// </remarks>
internal interface IRecordingShell
{
    ShellPage SelectedPage { get; set; }

    string StatusMessage { get; set; }

    bool IsRecording { get; set; }

    bool IsTransitioning { get; set; }
}

/// <summary>
/// The slice of the shell a recorded-media import drives: its own progress row in the status bar,
/// plus the two flags that keep it and a live recording mutually exclusive.
/// </summary>
/// <remarks>Same contract-not-view-model rationale as <see cref="IRecordingShell"/>.</remarks>
internal interface IVideoImportShell
{
    ShellPage SelectedPage { get; set; }

    string StatusMessage { get; set; }

    bool IsRecording { get; }

    bool CanImportVideo { get; }

    bool IsImportingVideo { get; set; }

    double ImportVideoProgress { get; set; }

    bool IsImportVideoProgressIndeterminate { get; set; }

    string ImportVideoStatusText { get; set; }
}

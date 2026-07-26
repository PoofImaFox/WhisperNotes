using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using WhisperNotes.App.Composition;
using WhisperNotes.App.ViewModels;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.Shell;

/// <summary>
/// The second way audio becomes a session: a recording already on disk. Core owns extraction,
/// chunked decoding, anonymous speaker attribution, cleanup and finalization; this owns the
/// desktop state around it — the progress row, the device hand-over, cancellation, and what the
/// user is told when it ends.
/// </summary>
internal sealed class VideoImportController(
    AppServices services,
    IVideoImportShell shell,
    CaptureViewModel capture,
    SessionDocumentViewModel document,
    NotesBrowserViewModel browser,
    ShellSettings settings,
    ShellNotificationCenter notifications) : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _importTask;
    private bool _isShuttingDown;

    /// <summary>Cancels from the toolbar. Whatever has already been decoded is still finalized.</summary>
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// The window is closing: stop handing the devices back to the pre-flight monitor when an
    /// import unwinds, because nothing should reopen an endpoint on the way out.
    /// </summary>
    public void BeginShutdown() => _isShuttingDown = true;

    /// <summary>Cancels an import in flight and waits for it to finish unwinding.</summary>
    public async Task WaitForCompletionAsync()
    {
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(true);
        }

        if (_importTask is not null)
        {
            await _importTask.ConfigureAwait(true);
        }
    }

    /// <summary>Transcribes a local recording chosen by the view's native file picker.</summary>
    public async Task ImportAsync(string inputPath)
    {
        if (!shell.CanImportVideo)
        {
            return;
        }

        if (!TryPrepare(inputPath, out ModelOptionViewModel? model))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        shell.IsImportingVideo = true;
        shell.ImportVideoProgress = 0;
        shell.IsImportVideoProgressIndeterminate = true;
        shell.ImportVideoStatusText = "Inspecting the recording…";
        shell.SelectedPage = ShellPage.Meeting;

        // Handed to the shutdown path so a closing window waits for the unwind, not just the cancel.
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _importTask = completion.Task;

        try
        {
            await TranscribeAsync(inputPath, model, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            shell.StatusMessage = "Video import cancelled before transcription started.";
            notifications.Post(
                "Video import cancelled",
                "No transcript session was created.",
                NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            shell.StatusMessage = "Video import failed.";
            notifications.Post(
                "Could not import that recording",
                ex.Message,
                NotificationSeverity.Error);

            // A failure after the session was created still finalizes whatever was decoded.
            await browser.RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            await ResetAsync(cancellation, completion).ConfigureAwait(true);
        }
    }

    /// <summary>Everything that must be true before a device is released and a session created.</summary>
    private bool TryPrepare(string inputPath, [NotNullWhen(true)] out ModelOptionViewModel? model)
    {
        model = capture.SelectedModel;
        if (model is null)
        {
            notifications.Post(
                "Pick a model",
                "Choose which whisper weights should decode the recording.",
                NotificationSeverity.Warning);
            return false;
        }

        if (!model.IsDownloaded)
        {
            notifications.Post(
                $"{model.Name} is not downloaded",
                $"Download the weights ({model.SizeText}) before importing this recording.",
                NotificationSeverity.Warning,
                actionLabel: "Download now",
                action: () => capture.DownloadModelCommand.Execute(null));
            return false;
        }

        if (!File.Exists(inputPath))
        {
            notifications.Post(
                "Could not open that recording",
                "The selected file no longer exists.",
                NotificationSeverity.Error);
            return false;
        }

        return true;
    }

    private async Task TranscribeAsync(
        string inputPath,
        ModelOptionViewModel model,
        CancellationToken cancellationToken)
    {
        await capture.StopMonitoringAsync().ConfigureAwait(true);

        var request = new RecordedMediaTranscriptionRequest(
            InputPath: inputPath,
            Title: Path.GetFileNameWithoutExtension(inputPath),
            Project: string.IsNullOrWhiteSpace(capture.Project) ? settings.DefaultProject : capture.Project.Trim(),
            Tags: [],
            ModelUsed: model.Name,
            Transcription: settings.Transcription,
            Diarization: settings.Diarization,
            StreamIndex: null,
            KeepAudio: settings.KeepSessionAudio);

        var progress = new Progress<RecordedMediaTranscriptionProgress>(UpdateProgress);
        Task<RecordedMediaTranscriptionResult> import = Task.Run(
            () => services.RecordedMedia.TranscribeAsync(request, progress, entries: null, cancellationToken),
            CancellationToken.None);

        RecordedMediaTranscriptionResult result = await import.ConfigureAwait(true);

        document.ShowReadOnlySession(result.Session, result.Entries);
        await browser.RefreshAndSelectAsync(result.Session.Id).ConfigureAwait(true);

        ReportCompleted(inputPath, result);
    }

    /// <summary>
    /// A cancelled import is a success with less in it: the partial transcript is on disk and is
    /// reported as such, rather than as a failure the user might assume lost their work.
    /// </summary>
    private void ReportCompleted(string inputPath, RecordedMediaTranscriptionResult result)
    {
        var directory = services.Notes.GetSessionDirectory(result.Session.Id);
        shell.StatusMessage = result.WasCancelled
            ? $"Partial transcript saved to {Path.Combine(directory, FileSystemNoteRepository.NotesFileName)}"
            : $"Imported {Path.GetFileName(inputPath)}";

        notifications.Post(
            result.WasCancelled ? "Video import cancelled" : "Video transcript ready",
            result.WasCancelled
                ? $"{result.Entries.Count} entries were preserved in {directory}."
                : $"{result.Entries.Count} entries were written to {directory}.",
            result.WasCancelled ? NotificationSeverity.Warning : NotificationSeverity.Info,
            actionLabel: "Open folder",
            action: () => FolderLauncher.Open(directory, notifications));
    }

    private async Task ResetAsync(CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        // Guarded by identity: a second import may already have taken the slots over.
        if (ReferenceEquals(_importTask, completion.Task))
        {
            _importTask = null;
        }

        if (ReferenceEquals(_cancellation, cancellation))
        {
            _cancellation = null;
        }

        cancellation.Dispose();
        shell.IsImportingVideo = false;
        shell.IsImportVideoProgressIndeterminate = true;
        shell.ImportVideoProgress = 0;
        shell.ImportVideoStatusText = "";

        if (!_isShuttingDown && !shell.IsRecording)
        {
            await capture.StartMonitoringAsync().ConfigureAwait(true);
        }

        completion.TrySetResult();
    }

    /// <summary>Progress&lt;T&gt; captured the UI context, so this is already on the right thread.</summary>
    private void UpdateProgress(RecordedMediaTranscriptionProgress progress)
    {
        shell.IsImportVideoProgressIndeterminate = progress.Fraction is null;
        shell.ImportVideoProgress = progress.Fraction ?? 0;

        string timing = progress.Processed is { } processed && progress.Total is { } total
            ? $" {FormatImportTime(processed)} / {FormatImportTime(total)}"
            : string.Empty;

        shell.ImportVideoStatusText = progress.Stage switch
        {
            RecordedMediaTranscriptionStage.Probing => "Inspecting audio streams…",
            RecordedMediaTranscriptionStage.Extracting => $"Extracting audio…{timing}",
            RecordedMediaTranscriptionStage.PreparingSpeakers => "Preparing anonymous speaker recognition…",
            RecordedMediaTranscriptionStage.Transcribing => $"Transcribing…{timing}",
            RecordedMediaTranscriptionStage.Diarizing => "Identifying speaker changes…",
            RecordedMediaTranscriptionStage.Finalizing => "Writing transcript and notes…",
            RecordedMediaTranscriptionStage.Completed => progress.Detail ?? "Transcript ready.",
            _ => progress.Detail ?? "Importing…"
        };
    }

    private static string FormatImportTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        // CancelAsync rather than Cancel: registered continuations would otherwise run inline on
        // the UI thread, and this runs while the window is closing.
        await _cancellation.CancelAsync().ConfigureAwait(true);
        _cancellation.Dispose();
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using WhisperNotes.App.Composition;
using WhisperNotes.App.ViewModels;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.App.Shell;

/// <summary>
/// Owns a meeting from the record button down: whether it may start at all, the session folder it
/// lands in, the pipeline that fills it, and finalising it again — including when the pipeline
/// dies on its own rather than because the user asked it to.
/// </summary>
/// <remarks>
/// The <see cref="LiveCapturePipeline"/> it drives is created and kept here, so the shell never
/// sees the capture, transcription or diarization types the pipeline is built from.
/// </remarks>
internal sealed class RecordingSessionController(
    AppServices services,
    IRecordingShell shell,
    CaptureViewModel capture,
    SessionDocumentViewModel document,
    NotesBrowserViewModel browser,
    InputSettingsViewModel inputs,
    ShellSettings settings,
    ShellNotificationCenter notifications,
    InterimStatusReporter interim) : IAsyncDisposable
{
    private readonly LiveCapturePipeline _pipeline = new(
        services.CaptureSourceFactory,
        services.TranscriberFactory,
        services.Notes,
        services.SpeakerProfiles,
        capture.Meter.Report,
        entry =>
        {
            interim.MarkActivity();
            document.Append(entry);
        },
        document.ApplySpeakerLabels,
        notifications);

    private CancellationTokenSource? _recordingCts;
    private Task<Exception?>? _recordingTask;
    private NoteSession? _liveSession;
    private bool _stopInProgress;

    /// <summary>What the transport button does, whichever state it is in.</summary>
    public async Task ToggleAsync()
    {
        if (shell.IsRecording)
        {
            await StopAsync(faulted: false).ConfigureAwait(true);
        }
        else
        {
            await StartAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Finalises a session still in progress, so closing the window never loses a meeting.</summary>
    public async Task ShutdownAsync()
    {
        if (shell.IsRecording)
        {
            await StopAsync(faulted: false).ConfigureAwait(true);
        }
    }

    private async Task StartAsync()
    {
        IReadOnlyList<ConfiguredAudioInput> configuredInputs = inputs.EnabledInputs;
        if (!ValidateInputs(configuredInputs))
        {
            return;
        }

        if (!ValidateModel(out ModelOptionViewModel? model))
        {
            return;
        }

        shell.IsTransitioning = true;
        try
        {
            await BeginSessionAsync(configuredInputs, model).ConfigureAwait(true);
        }
        finally
        {
            shell.IsTransitioning = false;
        }
    }

    /// <summary>
    /// Pre-flight on the input set. Every refusal sends the user to the page that can fix it and
    /// says what to do there — a record button that silently declines is indistinguishable from a bug.
    /// </summary>
    private bool ValidateInputs(IReadOnlyList<ConfiguredAudioInput> configuredInputs)
    {
        if (inputs.HasMissingEnabledSources)
        {
            return RefuseOnInputsPage(
                "An enabled input is unavailable",
                "Reconnect the device, choose a replacement, or disable it before recording.");
        }

        if (configuredInputs.Count == 0)
        {
            return RefuseOnInputsPage(
                "Enable an audio input",
                "Add and enable at least one loopback or microphone input before recording.");
        }

        IGrouping<string, ConfiguredAudioInput>? duplicateDevice = configuredInputs
            .GroupBy(input => input.Channel.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDevice is not null)
        {
            return RefuseOnInputsPage(
                "One device is configured more than once",
                $"Choose different devices for {string.Join(" and ", duplicateDevice.Select(input => input.DisplayName))}.");
        }

        IGrouping<string, ConfiguredAudioInput>? duplicateName = configuredInputs
            .GroupBy(input => input.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            return RefuseOnInputsPage(
                "Enabled inputs need distinct names",
                $"Rename the inputs currently called “{duplicateName.Key}” so transcript sources stay identifiable.");
        }

        return true;
    }

    private bool RefuseOnInputsPage(string title, string message)
    {
        shell.SelectedPage = ShellPage.Inputs;
        notifications.Post(title, message, NotificationSeverity.Warning);
        return false;
    }

    private bool ValidateModel([NotNullWhen(true)] out ModelOptionViewModel? model)
    {
        model = capture.SelectedModel;
        if (model is null)
        {
            notifications.Post(
                "Pick a model",
                "Choose which whisper weights to decode with.",
                NotificationSeverity.Warning);
            return false;
        }

        if (!model.IsDownloaded)
        {
            notifications.Post(
                $"{model.Name} is not downloaded",
                $"Download the weights ({model.SizeText}) before recording, or pick a model that is already on disk.",
                NotificationSeverity.Warning,
                actionLabel: "Download now",
                action: () => capture.DownloadModelCommand.Execute(null));
            return false;
        }

        return true;
    }

    private async Task BeginSessionAsync(
        IReadOnlyList<ConfiguredAudioInput> configuredInputs,
        ModelOptionViewModel model)
    {
        // The Inputs page owns its settings and may have saved after this shell's startup
        // snapshot was loaded. Refresh before deriving transcription and diarization options.
        settings.Refresh();
        settings.SetModel(model.Size);
        settings.SetDefaultProject(string.IsNullOrWhiteSpace(capture.Project) ? null : capture.Project.Trim());
        await settings.SaveAsync().ConfigureAwait(true);

        if (await CreateSessionAsync(configuredInputs, model).ConfigureAwait(true) is not { } session)
        {
            return;
        }

        _liveSession = session;
        interim.MarkActivity();

        IReadOnlyDictionary<string, ISpeakerAttributor> attributors =
            await PrepareSpeakerAttributorsAsync(configuredInputs).ConfigureAwait(true);

        document.BeginLiveSession(session);
        browser.SelectedNode = null;

        await capture.StopMonitoringAsync().ConfigureAwait(true);
        capture.Meter.IsActive = true;
        capture.BeginRecordingIndicator();
        shell.IsRecording = true;
        shell.StatusMessage = configuredInputs.Count == 1
            ? $"Recording “{session.Title}” from {configuredInputs[0].DisplayName}."
            : $"Recording “{session.Title}” from {configuredInputs.Count} inputs in parallel.";

        _recordingCts = new CancellationTokenSource();
        var token = _recordingCts.Token;
        var run = new LiveCaptureRun(
            configuredInputs,
            session,
            attributors,
            settings.Transcription,
            settings.Chunking,
            settings.Diarization);

        _recordingTask = Task.Run(() => _pipeline.RunAsync(run, token), CancellationToken.None);
        _ = WatchPipelineAsync(_recordingTask);
    }

    private async Task<NoteSession?> CreateSessionAsync(
        IReadOnlyList<ConfiguredAudioInput> configuredInputs,
        ModelOptionViewModel model)
    {
        var title = string.IsNullOrWhiteSpace(capture.SessionTitle)
            ? string.Create(CultureInfo.CurrentCulture, $"Meeting {DateTimeOffset.Now:yyyy-MM-dd HH:mm}")
            : capture.SessionTitle.Trim();

        var sourceDescription = string.Join(
            "; ",
            configuredInputs.Select(input =>
                $"{(input.Channel.Kind == AudioChannelKind.Loopback ? "Loopback" : "Microphone")} " +
                $"[{input.DisplayName}]: {input.Channel.Name}"));

        try
        {
            return await services.Notes.CreateSessionAsync(
                title,
                settings.DefaultProject,
                sourceDescription,
                [],
                model.Name,
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            notifications.Post("Could not create the session folder", ex.Message, NotificationSeverity.Error);
            return null;
        }
    }

    /// <summary>
    /// One observer per loopback input. Loopback carries the room and is the only kind worth
    /// clustering; a microphone is already one known voice. Attributors cannot be shared, because
    /// their timelines and captured PCM belong to different devices. A failure here costs labels,
    /// never the transcript.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ISpeakerAttributor>> PrepareSpeakerAttributorsAsync(
        IReadOnlyList<ConfiguredAudioInput> configuredInputs)
    {
        Dictionary<string, ISpeakerAttributor> speakerAttributors = new(StringComparer.Ordinal);
        if (!settings.DiarizationEnabled)
        {
            return speakerAttributors;
        }

        shell.StatusMessage = "Preparing anonymous speaker recognition…";
        foreach (ConfiguredAudioInput input in configuredInputs.Where(
                     input => input.Channel.Kind == AudioChannelKind.Loopback))
        {
            try
            {
                ISpeakerAttributor attributor = await services.Diarizers
                    .CreateAsync(settings.Diarization, CancellationToken.None)
                    .ConfigureAwait(true);
                speakerAttributors[input.Id] = attributor;
            }
            catch (Exception ex)
            {
                notifications.Post(
                    $"Speaker labels are unavailable for {input.DisplayName}",
                    $"{ex.Message} That input will continue with the transcript only.",
                    NotificationSeverity.Warning);
            }
        }

        return speakerAttributors;
    }

    /// <summary>
    /// A pipeline that ends by itself ended badly. Say so, then close the session on the user's
    /// behalf — unless they are already stopping it, in which case this <em>is</em> the stop.
    /// </summary>
    private async Task WatchPipelineAsync(Task<Exception?> pipeline)
    {
        Exception? fault = await pipeline.ConfigureAwait(true);

        if (fault is not null && shell.IsRecording && !_stopInProgress)
        {
            notifications.Post(
                fault is AudioCaptureException ? "Audio device failed" : "Transcription stopped",
                $"{fault.Message} The session is being closed; everything captured so far is already saved.",
                NotificationSeverity.Error);

            await StopAsync(faulted: true).ConfigureAwait(true);
        }
    }

    private async Task StopAsync(bool faulted)
    {
        if (_stopInProgress || _liveSession is not { } session)
        {
            return;
        }

        _stopInProgress = true;
        shell.IsTransitioning = true;
        shell.StatusMessage = "Finalising session…";

        try
        {
            await DrainPipelineAsync().ConfigureAwait(true);

            shell.IsRecording = false;
            capture.EndRecordingIndicator();
            document.InterimText = null;

            await FinalizeSessionAsync(session, faulted).ConfigureAwait(true);

            _liveSession = null;
            await browser.RefreshAsync().ConfigureAwait(true);
            await capture.StartMonitoringAsync().ConfigureAwait(true);
        }
        finally
        {
            _stopInProgress = false;
            shell.IsTransitioning = false;
        }
    }

    /// <summary>Cancels the run and waits it out, so the devices are released before we re-monitor.</summary>
    private async Task DrainPipelineAsync()
    {
        if (_recordingCts is not null)
        {
            await _recordingCts.CancelAsync().ConfigureAwait(true);
        }

        if (_recordingTask is not null)
        {
            await _recordingTask.ConfigureAwait(true);
        }

        _recordingCts?.Dispose();
        _recordingCts = null;
        _recordingTask = null;
    }

    private async Task FinalizeSessionAsync(NoteSession session, bool faulted)
    {
        try
        {
            var finalized = await services.Notes
                .FinalizeSessionAsync(session.Id, CancellationToken.None)
                .ConfigureAwait(true);

            document.MarkFinalized(finalized);

            var directory = services.Notes.GetSessionDirectory(session.Id);
            shell.StatusMessage = $"Saved to {Path.Combine(directory, "notes.md")}";
            notifications.Post(
                faulted ? "Session closed after an error" : "Session saved",
                $"{document.EntryCountText} written to {directory}",
                faulted ? NotificationSeverity.Warning : NotificationSeverity.Info,
                actionLabel: "Open folder",
                action: () => FolderLauncher.Open(directory, notifications));
        }
        catch (Exception ex)
        {
            notifications.Post(
                "Could not finalise the session",
                $"{ex.Message} The raw transcript is still on disk and can be re-rendered.",
                NotificationSeverity.Error);
        }
    }

    public ValueTask DisposeAsync()
    {
        // Not cancelled here: a session still rolling at this point was already stopped and
        // finalised by ShutdownAsync, which is the only path that may abandon one.
        _recordingCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}

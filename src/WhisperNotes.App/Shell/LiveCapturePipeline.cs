using Avalonia.Threading;
using WhisperNotes.App.Services;
using WhisperNotes.App.ViewModels;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.Shell;

/// <summary>Everything one live run needs, resolved on the UI thread before the first frame.</summary>
/// <param name="Inputs">The enabled inputs, already validated and de-duplicated.</param>
/// <param name="Session">The session folder the entries are appended to.</param>
/// <param name="SpeakerAttributors">Per-input observers, keyed by <see cref="ConfiguredAudioInput.Id"/>.
/// Sparse: an input without one simply goes unlabelled.</param>
internal sealed record LiveCaptureRun(
    IReadOnlyList<ConfiguredAudioInput> Inputs,
    NoteSession Session,
    IReadOnlyDictionary<string, ISpeakerAttributor> SpeakerAttributors,
    TranscriptionOptions Transcription,
    ChunkingOptions Chunking,
    DiarizationOptions Diarization);

/// <summary>
/// Capture -> engine -> disk -> UI, for however many inputs are rolling. Runs entirely off the UI
/// thread; only the committed-entry and speaker-label hand-offs are marshalled back.
/// </summary>
/// <remarks>
/// Knows nothing about the transport, the status line or the pages — it is handed a run and gives
/// back the fault that ended it, leaving the caller to decide whether that fault is worth telling
/// the user about (during a user-requested stop, it is just the stop).
/// </remarks>
/// <param name="reportPeak">The toolbar meter's sink. Fed the loudest of the inputs, see below.</param>
/// <param name="onEntryCommitted">Raised on the UI thread once the entry is safely on disk.</param>
/// <param name="onSpeakerLabels">Raised on the UI thread with entry id -> speaker label.</param>
internal sealed class LiveCapturePipeline(
    IAudioCaptureSourceFactory captureSources,
    ITranscriberFactory transcribers,
    INoteRepository notes,
    ISpeakerProfileStore speakerProfiles,
    Action<float> reportPeak,
    Action<NoteEntry> onEntryCommitted,
    Action<IReadOnlyDictionary<string, NoteEntry>> onSpeakerLabels,
    ShellNotificationCenter notifications)
{
    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> fires. Returns the fault that ended the run,
    /// or null for a clean stop. Never throws: teardown is guaranteed either way, and a caller that
    /// had to catch would be a caller that could skip it.
    /// </summary>
    public async Task<Exception?> RunAsync(LiveCaptureRun run, CancellationToken cancellationToken)
    {
        Dictionary<string, List<NoteEntry>> entriesBySource = run.Inputs.ToDictionary(
            input => input.Id,
            _ => new List<NoteEntry>(),
            StringComparer.Ordinal);

        List<IAudioCaptureSource> captureSourceList = [];
        Exception? fault = null;

        try
        {
            IReadOnlyList<LiveTranscriptionInput> liveInputs = OpenInputs(run, captureSourceList);
            await PumpAsync(run, liveInputs, entriesBySource, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            fault = await CloseAsync(run, captureSourceList, entriesBySource, fault).ConfigureAwait(false);
        }

        return fault;
    }

    /// <summary>
    /// Opens one metered capture source per input. Every input meters into its own slot; the
    /// toolbar shows the loudest of them, the same combined level the idle monitor was showing a
    /// moment ago.
    /// </summary>
    private IReadOnlyList<LiveTranscriptionInput> OpenInputs(LiveCaptureRun run, List<IAudioCaptureSource> opened)
    {
        var combinedMeter = new CombinedPeakMeter(run.Inputs.Count, reportPeak);

        List<LiveTranscriptionInput> liveInputs = [];
        for (var i = 0; i < run.Inputs.Count; i++)
        {
            ConfiguredAudioInput input = run.Inputs[i];
            IAudioCaptureSource source = new MeteringCaptureSource(
                captureSources.Create(input.Channel),
                combinedMeter.SinkFor(i));
            opened.Add(source);
            run.SpeakerAttributors.TryGetValue(input.Id, out ISpeakerAttributor? attributor);
            liveInputs.Add(new LiveTranscriptionInput(input.Id, input.DisplayName, source, attributor));
        }

        return liveInputs;
    }

    private async Task PumpAsync(
        LiveCaptureRun run,
        IReadOnlyList<LiveTranscriptionInput> liveInputs,
        Dictionary<string, List<NoteEntry>> entriesBySource,
        CancellationToken cancellationToken)
    {
        TranscriptionOptions options = ShareThreadBudget(run);
        var parallelEngine = new ParallelLiveTranscriptionEngine(transcribers, run.Chunking);

        await foreach (SourcedTranscriptSegment sourced in parallelEngine
                           .RunAsync(liveInputs, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            TranscriptSegment segment = sourced.Segment;
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            NoteEntry entry = ToEntry(run, sourced, segment);

            // Deliberately not the recording token: an utterance that has already been decoded
            // must reach disk even while we are shutting the session down.
            await notes.AppendEntryAsync(run.Session.Id, entry, CancellationToken.None).ConfigureAwait(false);
            entriesBySource[sourced.SourceId].Add(entry);

            Dispatcher.UIThread.Post(() => onEntryCommitted(entry));
        }
    }

    /// <summary>
    /// Treats <see cref="TranscriptionOptions.Threads"/> as the session's CPU budget. Giving every
    /// decoder the full single-input default would oversubscribe the machine as inputs are added.
    /// </summary>
    private static TranscriptionOptions ShareThreadBudget(LiveCaptureRun run)
    {
        if (run.Inputs.Count <= 1)
        {
            return run.Transcription;
        }

        int threadBudget = run.Transcription.Threads ?? Math.Min(Environment.ProcessorCount, 8);
        return run.Transcription with { Threads = Math.Max(1, threadBudget / run.Inputs.Count) };
    }

    private static NoteEntry ToEntry(LiveCaptureRun run, SourcedTranscriptSegment sourced, TranscriptSegment segment) =>
        new(
            Guid.NewGuid().ToString("n"),
            DateTimeOffset.Now,
            segment.Start,
            NoteEntryKind.Dictation,
            segment.Text.Trim(),
            // Multiple streams must stay distinguishable even when diarization concludes there was
            // only one voice. A microphone is also a deliberately isolated local voice, so its
            // user-chosen source name is more reliable than ML.
            Speaker: run.Inputs.Count > 1 || sourced.Channel.Kind == AudioChannelKind.Microphone
                ? sourced.SourceName
                : null,
            Confidence: segment.Confidence,
            EndOffset: segment.End);

    /// <summary>
    /// Teardown that must happen whether the run stopped cleanly or fell over: release the devices,
    /// then let each attributor label the entries it observed. Returns the first fault seen, so
    /// whatever ended the run is preferred over anything teardown adds on top of it.
    /// </summary>
    private async Task<Exception?> CloseAsync(
        LiveCaptureRun run,
        List<IAudioCaptureSource> opened,
        Dictionary<string, List<NoteEntry>> entriesBySource,
        Exception? fault)
    {
        foreach (IAudioCaptureSource source in opened)
        {
            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                fault ??= ex;
            }
        }

        bool qualifySpeakerNames = run.Inputs.Count > 1;
        foreach (ConfiguredAudioInput input in run.Inputs)
        {
            run.SpeakerAttributors.TryGetValue(input.Id, out ISpeakerAttributor? attributor);
            try
            {
                await AttributeSpeakersAsync(
                        run.Session,
                        entriesBySource[input.Id],
                        attributor,
                        qualifySpeakerNames ? input.DisplayName : null,
                        run.Diarization.ProfileMatchThreshold)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    attributor?.Dispose();
                }
                catch (Exception ex)
                {
                    fault ??= ex;
                }
            }
        }

        return fault;
    }

    /// <param name="sourceName">
    /// Prefixed to every label when more than one input is rolling — "Meeting audio · Speaker 2"
    /// — because two inputs both discovering a "Speaker 1" are not the same person.
    /// </param>
    private async Task AttributeSpeakersAsync(
        NoteSession session,
        IReadOnlyList<NoteEntry> entries,
        ISpeakerAttributor? speakerAttributor,
        string? sourceName,
        double profileMatchThreshold)
    {
        if (speakerAttributor is not { IsAvailable: true } ||
            speakerAttributor.Observed == 0 ||
            entries.Count == 0)
        {
            return;
        }

        try
        {
            SpeakerTimeline timeline = await Task.Run(speakerAttributor.Build).ConfigureAwait(false);
            await SpeakerAttribution
                .IdentifyProfilesAsync(
                    timeline,
                    speakerProfiles,
                    profileMatchThreshold,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!timeline.WorthLabelling && !timeline.HasNamedProfiles)
            {
                return;
            }

            Dictionary<string, NoteEntry> labels = [];
            foreach (NoteEntry entry in entries)
            {
                if (timeline.Label(entry.Offset, entry.EndOffset ?? entry.Offset) is { } speaker)
                {
                    string label = string.IsNullOrWhiteSpace(sourceName)
                        ? speaker
                        : $"{sourceName} · {speaker}";
                    SpeakerVoiceProfile? profile = timeline.Profile(
                        entry.Offset,
                        entry.EndOffset ?? entry.Offset);
                    NoteEntry attributed = entry with
                    {
                        Speaker = label,
                        SpeakerProfileId = profile?.Id,
                    };
                    await notes
                        .UpdateEntryAsync(
                            session.Id,
                            attributed,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    labels[entry.Id] = attributed;
                }
            }

            await Dispatcher.UIThread
                .InvokeAsync(() => onSpeakerLabels(labels))
                .GetTask()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => notifications.Post(
                "Could not identify speaker changes",
                $"{ex.Message} The transcript itself is complete.",
                NotificationSeverity.Warning));
        }
    }
}

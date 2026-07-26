using WhisperNotes.App.DesignData;
using WhisperNotes.Core.Ai;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Composition;
using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Media;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Notes.Documents;
using WhisperNotes.Core.Notes.Exporting;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.Composition;

/// <summary>
/// The single wiring point between the UI and Core. Everything above this class talks to
/// interfaces only, so the shell can be developed and run before Core lands.
/// </summary>
public sealed class AppServices
{
    public required IAudioChannelEnumerator ChannelEnumerator { get; init; }

    public required IAudioCaptureSourceFactory CaptureSourceFactory { get; init; }

    public required ILiveTranscriptionEngine TranscriptionEngine { get; init; }

    public required ITranscriberFactory TranscriberFactory { get; init; }

    public required IWhisperModelStore ModelStore { get; init; }

    public required ISpeakerAttributorFactory Diarizers { get; init; }

    public required IRecordedMediaTranscriptionService RecordedMedia { get; init; }

    public required INoteRepository Notes { get; init; }

    public required ISettingsStore Settings { get; init; }

    public required IMediaConverter Media { get; init; }

    /// <summary>Builds an assistant for the current AI settings. Rebuild after any settings change.</summary>
    public required IAiAssistantFactory AiAssistants { get; init; }

    /// <summary>Standalone note documents and their revision history.</summary>
    public required INoteDocumentStore Documents { get; init; }

    /// <summary>Portable single-note and whole-library exports.</summary>
    public required INoteExportService Exports { get; init; }

    /// <summary>True when the app is running on in-process sample data rather than real Core services.</summary>
    public bool IsSampleData { get; init; }

    /// <summary>Why we fell back to sample data, when we did. Null on the happy path.</summary>
    public string? SampleDataReason { get; init; }

    /// <summary>
    /// Owns teardown of the real Core graph — the loaded whisper weights and the open append
    /// handle on the active session's transcript. Null when running on fakes.
    /// </summary>
    public WhisperNotesServices? Core { get; init; }

    /// <summary>Fakes with plausible content — used by the Avalonia designer and by
    /// <see cref="CreateDefault"/> until <see cref="CreateRuntime"/> is wired.</summary>
    public static AppServices CreateDesignTime()
    {
        var settings = new FakeSettingsStore();
        var notes = new FakeNoteRepository();

        return new AppServices
        {
            ChannelEnumerator = new FakeAudioChannelEnumerator(),
            CaptureSourceFactory = new FakeAudioCaptureSourceFactory(),
            TranscriptionEngine = new FakeLiveTranscriptionEngine(),
            TranscriberFactory = new FakeTranscriberFactory(),
            ModelStore = new FakeWhisperModelStore(),
            Diarizers = new FakeSpeakerAttributorFactory(),
            RecordedMedia = new FakeRecordedMediaTranscriptionService(notes),
            Notes = notes,
            Settings = settings,
            Media = new FakeMediaConverter(),
            AiAssistants = new FakeAiAssistantFactory(),
            Documents = new FakeNoteDocumentStore(),
            Exports = new NoteExportService(),
            IsSampleData = true,
        };
    }

    /// <summary>The real Core graph, built from the on-disk settings.</summary>
    public static AppServices CreateRuntime()
    {
        var settingsStore = new JsonSettingsStore();
        AppSettings settings = settingsStore.Load();
        WhisperNotesServices core = WhisperNotesServices.Create(settings, settingsStore: settingsStore);

        return new AppServices
        {
            ChannelEnumerator = core.Channels,
            CaptureSourceFactory = core.CaptureSources,
            TranscriptionEngine = core.LiveEngine,
            TranscriberFactory = core.Transcribers,
            ModelStore = core.Models,
            Diarizers = core.Diarizers,
            RecordedMedia = core.RecordedMedia,
            Notes = core.Notes,
            Settings = core.SettingsStore,
            Media = core.Media,
            AiAssistants = core.AiAssistants,
            Documents = core.Documents,
            Exports = new NoteExportService(),
            Core = core,
        };
    }

    /// <summary>
    /// Real services when they exist, sample data otherwise. The fallback is deliberate and loud:
    /// <see cref="IsSampleData"/> drives a permanent banner so nobody mistakes fake notes for a
    /// billable record.
    /// </summary>
    public static AppServices CreateDefault()
    {
        try
        {
            return CreateRuntime();
        }
        catch (Exception ex)
        {
            // Constructing Core touches WASAPI and the notes root. If either is unusable the app
            // still opens — but on visibly fake data, never on a half-working real graph that
            // might quietly fail to record a client call.
            System.Diagnostics.Trace.TraceError($"Core services unavailable, using sample data: {ex}");

            AppServices fallback = CreateDesignTime();
            return new AppServices
            {
                ChannelEnumerator = fallback.ChannelEnumerator,
                CaptureSourceFactory = fallback.CaptureSourceFactory,
                TranscriptionEngine = fallback.TranscriptionEngine,
                TranscriberFactory = fallback.TranscriberFactory,
                ModelStore = fallback.ModelStore,
                Diarizers = fallback.Diarizers,
                RecordedMedia = fallback.RecordedMedia,
                Notes = fallback.Notes,
                Settings = fallback.Settings,
                Media = fallback.Media,
                AiAssistants = fallback.AiAssistants,
                Documents = fallback.Documents,
                Exports = fallback.Exports,
                IsSampleData = true,
                SampleDataReason = ex.Message,
            };
        }
    }
}

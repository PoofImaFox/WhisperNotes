using NoteScribe.App.DesignData;
using NoteScribe.Core.Audio;
using NoteScribe.Core.Composition;
using NoteScribe.Core.Configuration;
using NoteScribe.Core.Media;
using NoteScribe.Core.Notes;
using NoteScribe.Core.Transcription;

namespace NoteScribe.App.Composition;

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

    public required INoteRepository Notes { get; init; }

    public required ISettingsStore Settings { get; init; }

    public required IMediaConverter Media { get; init; }

    /// <summary>True when the app is running on in-process sample data rather than real Core services.</summary>
    public bool IsSampleData { get; init; }

    /// <summary>Why we fell back to sample data, when we did. Null on the happy path.</summary>
    public string? SampleDataReason { get; init; }

    /// <summary>
    /// Owns teardown of the real Core graph — the loaded whisper weights and the open append
    /// handle on the active session's transcript. Null when running on fakes.
    /// </summary>
    public NoteScribeServices? Core { get; init; }

    /// <summary>Fakes with plausible content — used by the Avalonia designer and by
    /// <see cref="CreateDefault"/> until <see cref="CreateRuntime"/> is wired.</summary>
    public static AppServices CreateDesignTime()
    {
        var settings = new FakeSettingsStore();

        return new AppServices
        {
            ChannelEnumerator = new FakeAudioChannelEnumerator(),
            CaptureSourceFactory = new FakeAudioCaptureSourceFactory(),
            TranscriptionEngine = new FakeLiveTranscriptionEngine(),
            TranscriberFactory = new FakeTranscriberFactory(),
            ModelStore = new FakeWhisperModelStore(),
            Notes = new FakeNoteRepository(),
            Settings = settings,
            Media = new FakeMediaConverter(),
            IsSampleData = true,
        };
    }

    /// <summary>The real Core graph, built from the on-disk settings.</summary>
    public static AppServices CreateRuntime()
    {
        var settingsStore = new JsonSettingsStore();
        AppSettings settings = settingsStore.Load();
        NoteScribeServices core = NoteScribeServices.Create(settings, settingsStore: settingsStore);

        return new AppServices
        {
            ChannelEnumerator = core.Channels,
            CaptureSourceFactory = core.CaptureSources,
            TranscriptionEngine = core.LiveEngine,
            TranscriberFactory = core.Transcribers,
            ModelStore = core.Models,
            Notes = core.Notes,
            Settings = core.SettingsStore,
            Media = core.Media,
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
                Notes = fallback.Notes,
                Settings = fallback.Settings,
                Media = fallback.Media,
                IsSampleData = true,
                SampleDataReason = ex.Message,
            };
        }
    }
}

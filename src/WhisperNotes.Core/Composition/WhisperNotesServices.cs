using System.Diagnostics.CodeAnalysis;
using WhisperNotes.Core.Ai;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Media;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Notes.Documents;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Composition;

/// <summary>
/// The single wiring point for the whole application. Both the CLI and the Avalonia app build
/// one of these from an <see cref="AppSettings"/> and use nothing but the interfaces on it.
/// </summary>
/// <remarks>
/// Two of the services hold expensive or unflushed state — <see cref="ILiveTranscriptionEngine"/>
/// keeps the loaded whisper weights resident, and <see cref="INoteRepository"/> keeps an open
/// append handle per active session — but neither interface declares disposal. Owning construction
/// here means we also own teardown: <see cref="DisposeAsync"/> disposes whatever is disposable, so
/// callers never have to know which concrete types happen to need it.
/// </remarks>
public sealed class WhisperNotesServices : IAsyncDisposable
{
    private bool _disposed;

    private readonly Lazy<INoteRepository> _notes;
    private readonly Lazy<INoteDocumentStore> _documents;
    private readonly Lazy<IRecordedMediaTranscriptionService> _recordedMedia;

    private WhisperNotesServices(
        AppSettings settings,
        ISettingsStore settingsStore,
        IAudioChannelEnumerator channels,
        IAudioCaptureSourceFactory captureSources,
        IWhisperModelStore models,
        ITranscriberFactory transcribers,
        ILiveTranscriptionEngine liveEngine,
        IMediaConverter media,
        IWavReader wavReader,
        ISpeakerAttributorFactory diarizers,
        Lazy<INoteRepository> notes,
        Lazy<INoteDocumentStore> documents,
        INoteExporter exporter,
        IAiAssistantFactory aiAssistants)
    {
        Settings = settings;
        SettingsStore = settingsStore;
        Channels = channels;
        CaptureSources = captureSources;
        Models = models;
        Transcribers = transcribers;
        LiveEngine = liveEngine;
        Media = media;
        WavReader = wavReader;
        Diarizers = diarizers;
        _notes = notes;
        _documents = documents;
        _recordedMedia = new Lazy<IRecordedMediaTranscriptionService>(() =>
            new RecordedMediaTranscriptionService(Media, WavReader, Transcribers, Diarizers, Notes));
        Exporter = exporter;
        AiAssistants = aiAssistants;
    }

    public AppSettings Settings { get; }
    public ISettingsStore SettingsStore { get; }
    public IAudioChannelEnumerator Channels { get; }
    public IAudioCaptureSourceFactory CaptureSources { get; }
    public IWhisperModelStore Models { get; }
    public ITranscriberFactory Transcribers { get; }
    public ILiveTranscriptionEngine LiveEngine { get; }
    public IMediaConverter Media { get; }
    public IWavReader WavReader { get; }
    public INoteExporter Exporter { get; }

    /// <summary>
    /// Complete recorded audio/video ingest. Lazy so read-only commands do not create the notes
    /// root merely by constructing the service graph.
    /// </summary>
    public IRecordedMediaTranscriptionService RecordedMedia => _recordedMedia.Value;

    /// <summary>
    /// Builds the speaker attributor. Held as a factory rather than an instance because the model
    /// is only worth loading for a run that actually asked for speaker labels.
    /// </summary>
    public ISpeakerAttributorFactory Diarizers { get; }

    /// <summary>
    /// Builds an assistant for the current <see cref="AiSettings"/>. Held rather than resolved
    /// because the UI rebuilds the assistant on every settings change; the factory itself is free.
    /// </summary>
    public IAiAssistantFactory AiAssistants { get; }

    /// <summary>
    /// Constructed on first touch. Building the repository creates the notes root on disk, and
    /// read-only commands like <c>devices</c> or <c>models path</c> have no business doing that.
    /// </summary>
    public INoteRepository Notes => _notes.Value;

    /// <summary>
    /// Standalone note documents and their revision history. Lazy for the same reason
    /// <see cref="Notes"/> is: constructing the store creates <c>_documents/</c> on disk, which a
    /// read-only CLI command must not do.
    /// </summary>
    public INoteDocumentStore Documents => _documents.Value;

    /// <summary>
    /// Builds the real service graph.
    /// </summary>
    /// <param name="settings">Effective settings, already merged with any command line overrides.</param>
    /// <param name="downloadProgress">
    /// Reported while whisper weights download. This is wired at construction rather than per call
    /// because <see cref="ITranscriberFactory.CreateAsync"/> has no progress parameter, and a silent
    /// multi-gigabyte download at the moment a meeting starts is the worst possible time to have none.
    /// </param>
    /// <param name="settingsStore">Override for tests; defaults to the on-disk JSON store.</param>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Deliberate ownership transfer: the engine is handed to the returned " +
                        "WhisperNotesServices, whose DisposeAsync disposes it. Disposing it here would " +
                        "unload the whisper weights the caller is about to use.")]
    public static WhisperNotesServices Create(
        AppSettings settings,
        IProgress<ModelDownloadProgress>? downloadProgress = null,
        ISettingsStore? settingsStore = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var exporter = new MarkdownNoteExporter();
        var models = new WhisperModelStore(settings.ModelsRoot);
        var transcribers = new WhisperTranscriberFactory(models, downloadProgress);

        return new WhisperNotesServices(
            settings,
            settingsStore ?? new JsonSettingsStore(),
            new WasapiChannelEnumerator(),
            new WasapiCaptureSourceFactory(),
            models,
            transcribers,
            new LiveTranscriptionEngine(transcribers, settings.ToChunkingOptions()),
            new FfmpegMediaConverter(settings.FfmpegPath),
            new WaveFileReaderService(),
            new OnnxSpeakerAttributorFactory(new SpeakerModelStore(settings.ModelsRoot), downloadProgress),
            new Lazy<INoteRepository>(() => new FileSystemNoteRepository(settings.NotesRoot, exporter)),
            new Lazy<INoteDocumentStore>(() => new FileSystemNoteDocumentStore(settings.NotesRoot)),
            exporter,
            new AiAssistantFactory());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DisposeIfNeededAsync(LiveEngine).ConfigureAwait(false);

        // Only if something actually asked for it — touching it here would defeat the laziness.
        if (_notes.IsValueCreated)
        {
            await DisposeIfNeededAsync(_notes.Value).ConfigureAwait(false);
        }

        if (_documents.IsValueCreated)
        {
            await DisposeIfNeededAsync(_documents.Value).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeIfNeededAsync(object service)
    {
        switch (service)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}

using NoteScribe.Core.Audio;
using NoteScribe.Core.Configuration;
using NoteScribe.Core.Media;
using NoteScribe.Core.Notes;
using NoteScribe.Core.Transcription;

namespace NoteScribe.Core.Composition;

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
public sealed class NoteScribeServices : IAsyncDisposable
{
    private bool _disposed;

    private readonly Lazy<INoteRepository> _notes;

    private NoteScribeServices(
        AppSettings settings,
        ISettingsStore settingsStore,
        IAudioChannelEnumerator channels,
        IAudioCaptureSourceFactory captureSources,
        IWhisperModelStore models,
        ITranscriberFactory transcribers,
        ILiveTranscriptionEngine liveEngine,
        IMediaConverter media,
        IWavReader wavReader,
        Lazy<INoteRepository> notes,
        INoteExporter exporter)
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
        _notes = notes;
        Exporter = exporter;
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
    /// Constructed on first touch. Building the repository creates the notes root on disk, and
    /// read-only commands like <c>devices</c> or <c>models path</c> have no business doing that.
    /// </summary>
    public INoteRepository Notes => _notes.Value;

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
    public static NoteScribeServices Create(
        AppSettings settings,
        IProgress<ModelDownloadProgress>? downloadProgress = null,
        ISettingsStore? settingsStore = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var exporter = new MarkdownNoteExporter();
        var models = new WhisperModelStore(settings.ModelsRoot);
        var transcribers = new WhisperTranscriberFactory(models, downloadProgress);

        return new NoteScribeServices(
            settings,
            settingsStore ?? new JsonSettingsStore(),
            new WasapiChannelEnumerator(),
            new WasapiCaptureSourceFactory(),
            models,
            transcribers,
            new LiveTranscriptionEngine(transcribers, settings.ToChunkingOptions()),
            new FfmpegMediaConverter(settings.FfmpegPath),
            new WaveFileReaderService(),
            new Lazy<INoteRepository>(() => new FileSystemNoteRepository(settings.NotesRoot, exporter)),
            exporter);
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

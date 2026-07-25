namespace NoteScribe.Core.Transcription;

/// <summary>
/// Resolves the requested weights through an <see cref="IWhisperModelStore"/> — downloading them on
/// first use — and loads them into a <see cref="WhisperTranscriber"/>.
/// </summary>
public sealed class WhisperTranscriberFactory : ITranscriberFactory
{
    private readonly IWhisperModelStore _modelStore;
    private readonly IProgress<ModelDownloadProgress>? _downloadProgress;

    public WhisperTranscriberFactory(
        IWhisperModelStore modelStore,
        IProgress<ModelDownloadProgress>? downloadProgress = null)
    {
        ArgumentNullException.ThrowIfNull(modelStore);
        _modelStore = modelStore;
        _downloadProgress = downloadProgress;
    }

    public async Task<ITranscriber> CreateAsync(TranscriptionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string modelPath = await _modelStore
            .EnsureDownloadedAsync(options.Model, _downloadProgress, cancellationToken)
            .ConfigureAwait(false);

        // Loading the weights is a synchronous native call that reads the whole file — up to 3 GB
        // for large-v3 — so it must not run on a UI thread.
        return await Task.Run(() => (ITranscriber)WhisperTranscriber.Create(modelPath, options), cancellationToken)
            .ConfigureAwait(false);
    }
}

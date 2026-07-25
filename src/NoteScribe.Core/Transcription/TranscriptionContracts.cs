using NoteScribe.Core.Audio;

namespace NoteScribe.Core.Transcription;

/// <summary>Which ggml weights to run.</summary>
public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,
    LargeV3,
    LargeV3Turbo
}

/// <summary>One decoded span of speech.</summary>
/// <param name="Start">Offset from the start of the session/media.</param>
/// <param name="End">Offset from the start of the session/media.</param>
/// <param name="Text">Decoded text, already trimmed.</param>
/// <param name="Confidence">0..1. Use 0 when the backend does not report one.</param>
/// <param name="Language">Detected or forced language code, e.g. "en".</param>
public sealed record TranscriptSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    float Confidence,
    string? Language)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Decodes normalised 16 kHz mono float PCM into text.</summary>
public interface ITranscriber : IAsyncDisposable
{
    /// <summary>
    /// Decodes one buffer. <paramref name="offset"/> is added to every segment timestamp so
    /// callers get session-relative times rather than buffer-relative ones.
    /// </summary>
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        ReadOnlyMemory<float> pcm16kMono,
        TimeSpan offset,
        CancellationToken cancellationToken);
}

/// <summary>Builds a transcriber, loading weights lazily.</summary>
public interface ITranscriberFactory
{
    Task<ITranscriber> CreateAsync(TranscriptionOptions options, CancellationToken cancellationToken);
}

/// <param name="Model">Which weights to load.</param>
/// <param name="Language">ISO code, or "auto" to detect.</param>
/// <param name="Threads">Decoder threads; null means "pick a sensible default".</param>
/// <param name="Translate">Translate to English rather than transcribing verbatim.</param>
/// <param name="InitialPrompt">Domain vocabulary hint — useful for client/product names.</param>
public sealed record TranscriptionOptions(
    WhisperModelSize Model = WhisperModelSize.Base,
    string Language = "auto",
    int? Threads = null,
    bool Translate = false,
    string? InitialPrompt = null);

/// <summary>Resolves and downloads ggml weight files.</summary>
public interface IWhisperModelStore
{
    string GetModelPath(WhisperModelSize size);

    bool IsDownloaded(WhisperModelSize size);

    /// <summary>Downloads the weights if absent and returns the local path. Must be resumable-safe
    /// (download to a temp file, then move) so a cancelled download never leaves a corrupt model.</summary>
    Task<string> EnsureDownloadedAsync(
        WhisperModelSize size,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <param name="BytesRead">Bytes downloaded so far.</param>
/// <param name="TotalBytes">Total expected, or null when the server omits Content-Length.</param>
public readonly record struct ModelDownloadProgress(long BytesRead, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesRead / TotalBytes.Value : null;
}

/// <summary>
/// Drives the live pipeline: capture -> buffer -> segment on silence -> decode.
/// </summary>
public interface ILiveTranscriptionEngine
{
    /// <summary>
    /// Streams segments as they are decoded. Completes when <paramref name="cancellationToken"/>
    /// fires, after flushing whatever audio is still buffered.
    /// </summary>
    IAsyncEnumerable<TranscriptSegment> RunAsync(
        IAudioCaptureSource source,
        TranscriptionOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Tuning for how live audio is cut into decodable chunks.</summary>
/// <param name="MinChunk">Never decode a chunk shorter than this.</param>
/// <param name="MaxChunk">Force a decode once a chunk reaches this, even mid-sentence.</param>
/// <param name="SilenceDuration">How much quiet marks a natural cut point.</param>
/// <param name="SilenceThreshold">RMS below this counts as silence. 0..1.</param>
public sealed record ChunkingOptions(
    TimeSpan MinChunk,
    TimeSpan MaxChunk,
    TimeSpan SilenceDuration,
    float SilenceThreshold)
{
    public static ChunkingOptions Default { get; } = new(
        MinChunk: TimeSpan.FromSeconds(2),
        MaxChunk: TimeSpan.FromSeconds(15),
        SilenceDuration: TimeSpan.FromMilliseconds(700),
        SilenceThreshold: 0.006f);
}

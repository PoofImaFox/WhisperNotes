namespace WhisperNotes.Core.Media;

/// <summary>One audio stream discovered inside a media container.</summary>
/// <param name="Index">ffmpeg stream index, used as <c>-map 0:&lt;Index&gt;</c>.</param>
/// <param name="CodecName">e.g. "aac".</param>
/// <param name="Channels">Channel count of the source stream.</param>
/// <param name="SampleRate">Sample rate of the source stream.</param>
/// <param name="Language">ISO language tag when the container carries one.</param>
/// <param name="Title">Stream title tag when present — often names the speaker or feed.</param>
public sealed record MediaAudioStream(
    int Index,
    string CodecName,
    int Channels,
    int SampleRate,
    string? Language,
    string? Title)
{
    public string Describe() =>
        $"#{Index} {CodecName} {Channels}ch {SampleRate}Hz" +
        (Language is { Length: > 0 } l ? $" [{l}]" : string.Empty) +
        (Title is { Length: > 0 } t ? $" \"{t}\"" : string.Empty);
}

/// <param name="Processed">How much of the input has been converted.</param>
/// <param name="Total">Total duration, or null if unknown.</param>
public readonly record struct ConversionProgress(TimeSpan Processed, TimeSpan? Total)
{
    public double? Fraction =>
        Total is { Ticks: > 0 } t ? Math.Clamp(Processed.TotalSeconds / t.TotalSeconds, 0, 1) : null;
}

/// <summary>Wraps ffmpeg/ffprobe for the --video ingest path.</summary>
public interface IMediaConverter
{
    /// <summary>True when a usable ffmpeg and ffprobe were located.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Why <see cref="IsAvailable"/> is false, phrased for the user. Null when available.
    /// A configured-but-wrong path reads very differently from "not installed", and telling
    /// them apart is the difference between a five-second fix and a lost afternoon.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>Path to the resolved ffmpeg binary, for diagnostics.</summary>
    string? FfmpegPath { get; }

    /// <summary>Lists audio streams in the container so the CLI can offer a choice.</summary>
    Task<IReadOnlyList<MediaAudioStream>> ProbeAudioStreamsAsync(
        string inputPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extracts one audio stream to 16 kHz mono 16-bit PCM WAV — the format the transcriber wants.
    /// </summary>
    /// <param name="streamIndex">ffmpeg stream index, or null to take the first audio stream.</param>
    /// <returns>The path actually written.</returns>
    Task<string> ExtractAudioAsync(
        string inputPath,
        string outputWavPath,
        int? streamIndex,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Reads a 16 kHz mono WAV back as float samples for the transcriber.</summary>
public interface IWavReader
{
    /// <summary>Streams the file in chunks so large recordings never load fully into memory.</summary>
    IAsyncEnumerable<ReadOnlyMemory<float>> ReadChunksAsync(
        string wavPath,
        int chunkSamples,
        CancellationToken cancellationToken);

    /// <summary>Total duration of the file.</summary>
    TimeSpan GetDuration(string wavPath);
}

/// <summary>Raised when ffmpeg is missing or exits non-zero.</summary>
public sealed class MediaConversionException : Exception
{
    public MediaConversionException(string message, Exception? inner = null) : base(message, inner) { }
}

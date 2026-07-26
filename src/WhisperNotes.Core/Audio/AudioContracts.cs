namespace WhisperNotes.Core.Audio;

/// <summary>What kind of endpoint a channel represents.</summary>
public enum AudioChannelKind
{
    /// <summary>A render (output) endpoint captured via WASAPI loopback — this is how we hear Teams.</summary>
    Loopback,

    /// <summary>A capture (input) endpoint — microphone, line-in, virtual cable output.</summary>
    Microphone
}

/// <summary>A selectable audio source shown in the UI channel picker.</summary>
/// <param name="Id">Stable endpoint id, safe to persist in settings.</param>
/// <param name="Name">Friendly name, e.g. "Speakers (Realtek) [loopback]".</param>
/// <param name="Kind">Loopback or microphone.</param>
/// <param name="IsDefault">True when this is the system default endpoint for its role.</param>
/// <param name="NativeSampleRate">Endpoint's native mix rate, informational.</param>
/// <param name="NativeChannels">Endpoint's native channel count, informational.</param>
public sealed record AudioChannel(
    string Id,
    string Name,
    AudioChannelKind Kind,
    bool IsDefault,
    int NativeSampleRate,
    int NativeChannels);

/// <summary>
/// A block of audio normalised to the format Whisper requires:
/// 16 kHz, mono, 32-bit float, samples in [-1, 1].
/// </summary>
/// <param name="Samples">PCM samples. Ownership transfers to the consumer.</param>
/// <param name="Offset">Offset of the first sample from the start of capture.</param>
public readonly record struct AudioFrame(ReadOnlyMemory<float> Samples, TimeSpan Offset)
{
    /// <summary>Sample rate every <see cref="AudioFrame"/> in the system is normalised to.</summary>
    public const int SampleRate = 16_000;

    /// <summary>Channel count every <see cref="AudioFrame"/> in the system is normalised to.</summary>
    public const int Channels = 1;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

/// <summary>Lists the audio endpoints the user can pick from.</summary>
public interface IAudioChannelEnumerator
{
    /// <summary>Enumerates all active endpoints, loopback first, default endpoints first within each kind.</summary>
    IReadOnlyList<AudioChannel> GetChannels();

    /// <summary>Resolves a persisted id back to a live channel, or null if the device is gone.</summary>
    AudioChannel? Find(string channelId);
}

/// <summary>
/// A live audio tap on one channel. Implementations must resample/downmix to
/// <see cref="AudioFrame.SampleRate"/> mono float before yielding.
/// </summary>
public interface IAudioCaptureSource : IAsyncDisposable
{
    AudioChannel Channel { get; }

    /// <summary>
    /// Streams normalised audio until cancelled. Must complete (not throw) on cancellation.
    /// Device-loss should surface as an <see cref="AudioCaptureException"/>.
    /// </summary>
    IAsyncEnumerable<AudioFrame> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>Creates capture sources for a given channel.</summary>
public interface IAudioCaptureSourceFactory
{
    IAudioCaptureSource Create(AudioChannel channel);
}

/// <summary>Raised when a capture device fails or disappears mid-session.</summary>
public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException(string message, Exception? inner = null) : base(message, inner) { }
}

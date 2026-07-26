namespace WhisperNotes.Core.Audio;

/// <summary>What kind of endpoint a channel represents.</summary>
/// <remarks>
/// Values are appended, never reordered: <see cref="Configuration.InputSourceSettings.Kind"/> is
/// persisted to settings.json and older files must keep resolving to the same member.
/// </remarks>
public enum AudioChannelKind
{
    /// <summary>A render (output) endpoint captured via WASAPI loopback — this is how we hear Teams.</summary>
    Loopback,

    /// <summary>A capture (input) endpoint — microphone, line-in, virtual cable output.</summary>
    Microphone,

    /// <summary>
    /// A single application's render stream, captured via process loopback so nothing else playing
    /// on the machine bleeds into the transcript.
    /// </summary>
    /// <remarks>
    /// Requires Windows build 20348+ (see <see cref="ProcessLoopbackSupport"/>). On older systems the
    /// capture factory falls back to device-level loopback rather than failing the session.
    /// </remarks>
    Application
}

/// <summary>A selectable audio source shown in the UI channel picker.</summary>
/// <param name="Id">Stable endpoint id, safe to persist in settings.</param>
/// <param name="Name">Friendly name, e.g. "Speakers (Realtek) [loopback]".</param>
/// <param name="Kind">Loopback, microphone, or a single application.</param>
/// <param name="IsDefault">True when this is the system default endpoint for its role.</param>
/// <param name="NativeSampleRate">Endpoint's native mix rate, informational.</param>
/// <param name="NativeChannels">Endpoint's native channel count, informational.</param>
/// <param name="ProcessId">
/// Live process id for <see cref="AudioChannelKind.Application"/> channels, otherwise 0. Deliberately
/// excluded from <see cref="Id"/> because pids are recycled between runs — persist the executable name
/// and re-resolve the pid at capture time.
/// </param>
/// <param name="ExecutableName">
/// Lower-cased image name (e.g. <c>teams.exe</c>) for application channels, otherwise null. This is the
/// part that survives a restart.
/// </param>
public sealed record AudioChannel(
    string Id,
    string Name,
    AudioChannelKind Kind,
    bool IsDefault,
    int NativeSampleRate,
    int NativeChannels,
    int ProcessId = 0,
    string? ExecutableName = null);

/// <summary>
/// Encodes and decodes the <see cref="AudioChannel.Id"/> used by
/// <see cref="AudioChannelKind.Application"/> channels.
/// </summary>
/// <remarks>
/// Application ids are keyed on the executable name rather than the process id so that a saved input
/// still points at "Teams" after both the app and WhisperNotes have been restarted.
/// </remarks>
public static class ApplicationChannelId
{
    /// <summary>Prefix that distinguishes an application id from a WASAPI endpoint id.</summary>
    public const string Prefix = "app:";

    /// <summary>Builds the persistable id for an executable, e.g. <c>app:teams.exe</c>.</summary>
    public static string ForExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        return Prefix + executableName.Trim().ToLowerInvariant();
    }

    /// <summary>True when <paramref name="channelId"/> names an application rather than an endpoint.</summary>
    public static bool IsApplicationId(string? channelId) =>
        channelId is not null && channelId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Extracts the executable name from an application id, or null if it is not one.</summary>
    public static string? ExecutableOf(string? channelId) =>
        IsApplicationId(channelId) ? channelId![Prefix.Length..] : null;
}

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

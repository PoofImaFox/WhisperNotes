using System.Runtime.CompilerServices;
using NoteScribe.Core.Audio;

namespace NoteScribe.App.Services;

/// <summary>
/// Pass-through decorator that measures each frame on its way to the transcription engine.
/// The engine is the only consumer of a capture source, so metering has to ride along with it
/// rather than open a second tap on the same endpoint.
/// </summary>
internal sealed class MeteringCaptureSource(IAudioCaptureSource inner, Action<float> onPeak) : IAudioCaptureSource
{
    public AudioChannel Channel => inner.Channel;

    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in inner.CaptureAsync(cancellationToken).ConfigureAwait(false))
        {
            onPeak(AudioLevel.Peak(frame.Samples.Span));
            yield return frame;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal static class AudioLevel
{
    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return Math.Min(1f, peak);
    }
}

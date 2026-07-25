using System.Runtime.CompilerServices;
using NoteScribe.Core.Audio;

namespace NoteScribe.App.DesignData;

/// <summary>Creates fake taps. Microphone channels are deliberately quieter than loopback ones
/// so the level meter visibly distinguishes "wrong endpoint" from "right endpoint".</summary>
internal sealed class FakeAudioCaptureSourceFactory : IAudioCaptureSourceFactory
{
    public IAudioCaptureSource Create(AudioChannel channel) => new FakeAudioCaptureSource(channel);
}

internal sealed class FakeAudioCaptureSource(AudioChannel channel) : IAudioCaptureSource
{
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(50);
    private const int FrameSamples = (int)(AudioFrame.SampleRate * 0.05);

    public AudioChannel Channel { get; } = channel;

    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rng = new Random(Channel.Id.GetHashCode(StringComparison.Ordinal));
        // Offsets follow the wall clock rather than accumulating nominal frame durations, so the
        // timestamps in the UI stay honest even though Task.Delay overshoots.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phase = 0.0;

        // Loopback of a meeting is loud and near-continuous; an unused mic is close to the noise floor.
        var gain = Channel.Kind == AudioChannelKind.Loopback ? 0.55f : 0.06f;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await DelayAsync(cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }

            // Slow envelope models speech bursts separated by breaths; the meter should bounce, not sit flat.
            phase += 0.06;
            var envelope = (float)Math.Max(0.05, Math.Pow(Math.Abs(Math.Sin(phase)), 1.6) + rng.NextDouble() * 0.15 - 0.05);

            var buffer = new float[FrameSamples];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (float)((rng.NextDouble() * 2 - 1) * envelope * gain);
            }

            yield return new AudioFrame(buffer, clock.Elapsed);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Cancellation must complete the stream, never throw out of it (AudioContracts.cs).
    private static async Task<bool> DelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FrameDuration, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

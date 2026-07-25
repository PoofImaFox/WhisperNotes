using System.Runtime.CompilerServices;
using NoteScribe.Core.Audio;
using NoteScribe.Core.Transcription;

namespace NoteScribe.App.DesignData;

/// <summary>
/// Drains the capture source in real time (so the level meter still sees frames) and emits a
/// scripted segment roughly every few seconds, imitating silence-cut chunking.
/// </summary>
internal sealed class FakeLiveTranscriptionEngine : ILiveTranscriptionEngine
{
    public async IAsyncEnumerable<TranscriptSegment> RunAsync(
        IAudioCaptureSource source,
        TranscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rng = new Random(4242);
        var line = 0;
        var elapsed = TimeSpan.Zero;
        var chunkStart = TimeSpan.Zero;
        var nextCut = TimeSpan.FromSeconds(2.5);

        await foreach (var frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
        {
            elapsed = frame.Offset + frame.Duration;
            if (elapsed - chunkStart < nextCut)
            {
                continue;
            }

            var text = SampleData.DictationScript[line++ % SampleData.DictationScript.Count];
            yield return new TranscriptSegment(
                chunkStart,
                elapsed,
                text,
                0.61f + (float)rng.NextDouble() * 0.37f,
                options.Language == "auto" ? "en" : options.Language);

            chunkStart = elapsed;
            nextCut = TimeSpan.FromSeconds(2.0 + rng.NextDouble() * 3.5);
        }
    }
}

internal sealed class FakeTranscriberFactory : ITranscriberFactory
{
    public Task<ITranscriber> CreateAsync(TranscriptionOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<ITranscriber>(new FakeTranscriber(options));
}

internal sealed class FakeTranscriber(TranscriptionOptions options) : ITranscriber
{
    private int _line;

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        ReadOnlyMemory<float> pcm16kMono,
        TimeSpan offset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Decoding a buffer is not instant even on a fast machine; keep callers honest about awaiting.
        await Task.Yield();

        var duration = TimeSpan.FromSeconds((double)pcm16kMono.Length / AudioFrame.SampleRate);
        if (duration <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        yield return new TranscriptSegment(
            offset,
            offset + duration,
            SampleData.DictationScript[_line++ % SampleData.DictationScript.Count],
            0.78f,
            options.Language == "auto" ? "en" : options.Language);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

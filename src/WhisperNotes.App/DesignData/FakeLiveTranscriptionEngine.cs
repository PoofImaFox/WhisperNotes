using System.Runtime.CompilerServices;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.DesignData;

/// <summary>
/// Drains the capture source in real time (so the level meter still sees frames) and emits a
/// scripted segment roughly every few seconds, imitating silence-cut chunking.
/// </summary>
internal sealed class FakeLiveTranscriptionEngine : ILiveTranscriptionEngine
{
    public async IAsyncEnumerable<TranscriptSegment> RunAsync(
        IAudioCaptureSource source,
        TranscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ISpeakerAttributor? speakerAttributor = null)
    {
        var rng = new Random(4242);
        var line = 0;
        var chunkStart = TimeSpan.Zero;
        var nextCut = TimeSpan.FromSeconds(2.5);

        await foreach (var frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
        {
            var elapsed = frame.Offset + frame.Duration;
            if (elapsed - chunkStart < nextCut)
            {
                continue;
            }

            var text = SampleData.DictationScript[line++ % SampleData.DictationScript.Count];
            var segment = new TranscriptSegment(
                chunkStart,
                elapsed,
                text,
                0.61f + (float)rng.NextDouble() * 0.37f,
                options.Language == "auto" ? "en" : options.Language);

            speakerAttributor?.Observe(
                segment.Start,
                segment.End,
                frame.Samples.Span,
                frame.Offset);

            yield return segment;

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

/// <summary>Produces a deterministic A→B→A cast for the sample-data recording flow.</summary>
internal sealed class FakeSpeakerAttributorFactory : ISpeakerAttributorFactory
{
    public Task<ISpeakerAttributor> CreateAsync(
        DiarizationOptions options,
        CancellationToken cancellationToken) =>
        Task.FromResult<ISpeakerAttributor>(new FakeSpeakerAttributor(options.Enabled));
}

internal sealed class FakeSpeakerAttributor(bool enabled) : ISpeakerAttributor
{
    private readonly List<(TimeSpan Start, TimeSpan End)> _spans = [];

    public bool IsAvailable => enabled;

    public int Observed => _spans.Count;

    public void Observe(TimeSpan start, TimeSpan end, ReadOnlySpan<float> audio, TimeSpan audioOffset)
    {
        if (enabled && end > start)
        {
            _spans.Add((start, end));
        }
    }

    public SpeakerTimeline Build()
    {
        if (_spans.Count == 0)
        {
            return SpeakerTimeline.Unattributed;
        }

        SpeakerTurn[] turns = new SpeakerTurn[_spans.Count];
        for (var i = 0; i < turns.Length; i++)
        {
            // Two lines per turn makes the sample visibly show both a change and a return.
            var speaker = (i / 2) % 3 == 1 ? 1 : 0;
            turns[i] = new SpeakerTurn(_spans[i].Start, _spans[i].End, speaker);
        }

        return new SpeakerTimeline(turns, 2);
    }

    public void Dispose() => _spans.Clear();
}

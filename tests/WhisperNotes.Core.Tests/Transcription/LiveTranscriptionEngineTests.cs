using System.Runtime.CompilerServices;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Tests.Transcription;

public sealed class LiveTranscriptionEngineTests
{
    [Fact]
    public async Task RunAsync_ObservesThePcmBehindEachLiveSegment()
    {
        await using LiveTranscriptionEngine engine = new(new FakeTranscriberFactory());
        FakeSpeakerAttributor speakers = new();

        List<TranscriptSegment> segments = [];
        await foreach (TranscriptSegment segment in engine.RunAsync(
                           new SingleFrameSource(),
                           new TranscriptionOptions(),
                           CancellationToken.None,
                           speakers))
        {
            segments.Add(segment);
        }

        Assert.Single(segments);
        Assert.Equal(1, speakers.Observed);
        Assert.Equal(segments[0].Start, speakers.Start);
        Assert.Equal(segments[0].End, speakers.End);
        Assert.True(speakers.AudioSamples > 0);
    }

    private sealed class SingleFrameSource : IAudioCaptureSource
    {
        public AudioChannel Channel { get; } = new(
            "test",
            "Test",
            AudioChannelKind.Microphone,
            true,
            AudioFrame.SampleRate,
            AudioFrame.Channels);

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            float[] samples = new float[AudioFrame.SampleRate * 3];
            Array.Fill(samples, 0.1f);
            yield return new AudioFrame(samples, TimeSpan.Zero);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranscriberFactory : ITranscriberFactory
    {
        public Task<ITranscriber> CreateAsync(
            TranscriptionOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult<ITranscriber>(new FakeTranscriber());
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
            ReadOnlyMemory<float> pcm16kMono,
            TimeSpan offset,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            TimeSpan end = offset + TimeSpan.FromSeconds((double)pcm16kMono.Length / AudioFrame.SampleRate);
            yield return new TranscriptSegment(offset, end, "hello", 0.9f, "en");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSpeakerAttributor : ISpeakerAttributor
    {
        public bool IsAvailable => true;
        public int Observed { get; private set; }
        public TimeSpan Start { get; private set; }
        public TimeSpan End { get; private set; }
        public int AudioSamples { get; private set; }

        public void Observe(TimeSpan start, TimeSpan end, ReadOnlySpan<float> audio, TimeSpan audioOffset)
        {
            Observed++;
            Start = start;
            End = end;
            AudioSamples = audio.Length;
        }

        public SpeakerTimeline Build() => SpeakerTimeline.Unattributed;
        public void Dispose()
        {
        }
    }
}

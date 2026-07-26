using System.Runtime.CompilerServices;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Tests.Transcription;

public sealed class ParallelLiveTranscriptionEngineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunAsync_DecodesInputsConcurrentlyWithIndependentTranscribersAndProvenance()
    {
        CoordinatedTranscriberFactory transcribers = new(expectedConcurrentCalls: 2);
        ParallelLiveTranscriptionEngine engine = new(
            transcribers,
            new ChunkingOptions(
                MinChunk: TimeSpan.FromMilliseconds(100),
                MaxChunk: TimeSpan.FromMilliseconds(200),
                SilenceDuration: TimeSpan.FromSeconds(1),
                SilenceThreshold: 0.001f));

        LiveTranscriptionInput[] inputs =
        [
            new("meeting", "Meeting audio", new OneChunkSource("render", "Speakers")),
            new("self", "My microphone", new OneChunkSource("capture", "Microphone"))
        ];

        Task<List<SourcedTranscriptSegment>> run = CollectAsync(
            engine.RunAsync(inputs, new TranscriptionOptions(), CancellationToken.None));

        await transcribers.AllCallsStarted.WaitAsync(TestTimeout);
        Assert.Equal(2, transcribers.CreatedCount);

        transcribers.Release();
        List<SourcedTranscriptSegment> segments = await run.WaitAsync(TestTimeout);

        Assert.Equal(2, segments.Count);
        Assert.Contains(
            segments,
            result => result.SourceId == "meeting"
                      && result.SourceName == "Meeting audio"
                      && result.Channel.Id == "render");
        Assert.Contains(
            segments,
            result => result.SourceId == "self"
                      && result.SourceName == "My microphone"
                      && result.Channel.Id == "capture");
    }

    [Fact]
    public async Task RunAsync_CancellationStopsAndDisposesEveryInput()
    {
        SessionTracker tracker = new(expectedStarts: 2);
        ParallelLiveTranscriptionEngine engine = new(
            () => new WaitingEngine(tracker));
        using CancellationTokenSource cancellation = new();

        LiveTranscriptionInput[] inputs =
        [
            new("one", "One", new EmptySource("one")),
            new("two", "Two", new EmptySource("two"))
        ];

        Task<List<SourcedTranscriptSegment>> run = CollectAsync(
            engine.RunAsync(inputs, new TranscriptionOptions(), cancellation.Token));

        await tracker.AllStarted.WaitAsync(TestTimeout);
        await cancellation.CancelAsync();

        List<SourcedTranscriptSegment> results = await run.WaitAsync(TestTimeout);
        Assert.Empty(results);
        Assert.Equal(2, tracker.CancelledCount);
        Assert.Equal(2, tracker.DisposedCount);
    }

    [Fact]
    public async Task RunAsync_InputFailureCancelsSiblingsAndSurfacesOriginalFailure()
    {
        SessionTracker tracker = new(expectedStarts: 2);
        ParallelLiveTranscriptionEngine engine = new(
            () => new FaultingEngine(tracker));

        LiveTranscriptionInput[] inputs =
        [
            new("broken", "Broken input", new EmptySource("broken")),
            new("waiting", "Waiting input", new EmptySource("waiting"))
        ];

        Task<List<SourcedTranscriptSegment>> run = CollectAsync(
            engine.RunAsync(inputs, new TranscriptionOptions(), CancellationToken.None));

        await tracker.AllStarted.WaitAsync(TestTimeout);
        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() => run).WaitAsync(TestTimeout);

        Assert.Equal("broken input", failure.Message);
        Assert.Equal(1, tracker.CancelledCount);
        Assert.Equal(2, tracker.DisposedCount);
    }

    [Fact]
    public async Task RunAsync_RejectsDuplicateConfiguredSourceIds()
    {
        ParallelLiveTranscriptionEngine engine = new(() => new WaitingEngine(new SessionTracker(1)));
        LiveTranscriptionInput[] inputs =
        [
            new("same", "First", new EmptySource("first")),
            new("same", "Second", new EmptySource("second"))
        ];

        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            CollectAsync(engine.RunAsync(inputs, new TranscriptionOptions(), CancellationToken.None)));

        Assert.Contains("unique source id", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<SourcedTranscriptSegment>> CollectAsync(
        IAsyncEnumerable<SourcedTranscriptSegment> stream)
    {
        List<SourcedTranscriptSegment> results = [];
        await foreach (SourcedTranscriptSegment result in stream.ConfigureAwait(false))
        {
            results.Add(result);
        }

        return results;
    }

    private sealed class CoordinatedTranscriberFactory(int expectedConcurrentCalls) : ITranscriberFactory
    {
        private readonly int _expectedConcurrentCalls = expectedConcurrentCalls;
        private readonly TaskCompletionSource _allCallsStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _created;
        private int _started;

        public Task AllCallsStarted => _allCallsStarted.Task;
        public int CreatedCount => Volatile.Read(ref _created);

        public Task<ITranscriber> CreateAsync(
            TranscriptionOptions options,
            CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref _created);
            return Task.FromResult<ITranscriber>(new CoordinatedTranscriber(this, id));
        }

        public void Release() => _release.TrySetResult();

        private sealed class CoordinatedTranscriber(
            CoordinatedTranscriberFactory owner,
            int id) : ITranscriber
        {
            public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
                ReadOnlyMemory<float> pcm16kMono,
                TimeSpan offset,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref owner._started) == owner._expectedConcurrentCalls)
                {
                    owner._allCallsStarted.TrySetResult();
                }

                await owner._release.Task.ConfigureAwait(false);
                yield return new TranscriptSegment(
                    offset,
                    offset + TimeSpan.FromSeconds((double)pcm16kMono.Length / AudioFrame.SampleRate),
                    $"source {id}",
                    0.9f,
                    "en");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SessionTracker(int expectedStarts)
    {
        private readonly TaskCompletionSource _allStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _cancelled;
        private int _disposed;

        public Task AllStarted => _allStarted.Task;
        public int CancelledCount => Volatile.Read(ref _cancelled);
        public int DisposedCount => Volatile.Read(ref _disposed);

        public void Started()
        {
            if (Interlocked.Increment(ref _started) == expectedStarts)
            {
                _allStarted.TrySetResult();
            }
        }

        public void Cancelled() => Interlocked.Increment(ref _cancelled);
        public void Disposed() => Interlocked.Increment(ref _disposed);
    }

    private sealed class WaitingEngine(SessionTracker tracker) : ILiveTranscriptionEngine, IAsyncDisposable
    {
        public async IAsyncEnumerable<TranscriptSegment> RunAsync(
            IAudioCaptureSource source,
            TranscriptionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            ISpeakerAttributor? speakerAttributor = null)
        {
            tracker.Started();
            yield return await WaitForeverAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            tracker.Disposed();
            return ValueTask.CompletedTask;
        }

        private async Task<TranscriptSegment> WaitForeverAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Infinite delay completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                tracker.Cancelled();
                throw;
            }
        }
    }

    private sealed class FaultingEngine(SessionTracker tracker) : ILiveTranscriptionEngine, IAsyncDisposable
    {
        public async IAsyncEnumerable<TranscriptSegment> RunAsync(
            IAudioCaptureSource source,
            TranscriptionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            ISpeakerAttributor? speakerAttributor = null)
        {
            tracker.Started();

            if (source.Channel.Id == "broken")
            {
                yield return await FailAfterAllInputsStartAsync();
            }

            yield return await WaitForSiblingCancellationAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            tracker.Disposed();
            return ValueTask.CompletedTask;
        }

        private async Task<TranscriptSegment> FailAfterAllInputsStartAsync()
        {
            await tracker.AllStarted.ConfigureAwait(false);
            throw new InvalidOperationException("broken input");
        }

        private async Task<TranscriptSegment> WaitForSiblingCancellationAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Infinite delay completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                tracker.Cancelled();
                throw;
            }
        }
    }

    private sealed class OneChunkSource(string id, string name) : IAudioCaptureSource
    {
        public AudioChannel Channel { get; } = new(
            id,
            name,
            AudioChannelKind.Microphone,
            false,
            AudioFrame.SampleRate,
            AudioFrame.Channels);

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            float[] samples = new float[AudioFrame.SampleRate / 5];
            Array.Fill(samples, 0.1f);
            yield return new AudioFrame(samples, TimeSpan.Zero);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptySource(string id) : IAudioCaptureSource
    {
        public AudioChannel Channel { get; } = new(
            id,
            id,
            AudioChannelKind.Microphone,
            false,
            AudioFrame.SampleRate,
            AudioFrame.Channels);

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

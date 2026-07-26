using System.Runtime.CompilerServices;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Media;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Tests.Transcription;

public sealed class RecordedMediaTranscriptionServiceTests
{
    [Fact]
    public async Task TranscribeAsync_PersistsSpeakerLabels_FinalizesAndRemovesTemporaryWave()
    {
        await using TestContext context = await TestContext.CreateAsync(FakeTranscriberMode.Success);

        RecordedMediaTranscriptionResult result = await context.Service.TranscribeAsync(
            context.Request(diarize: true),
            progress: null,
            entries: null,
            CancellationToken.None);

        Assert.False(result.WasCancelled);
        Assert.NotNull(result.Session.EndedUtc);
        Assert.Equal(TimeSpan.FromSeconds(4), result.Session.Duration);
        Assert.Equal(["Speaker 1", "Speaker 2"], result.Entries.Select(entry => entry.Speaker));
        Assert.False(File.Exists(context.Converter.OutputPath));

        IReadOnlyList<NoteEntry> persisted = await context.Notes
            .LoadEntriesAsync(result.Session.Id, CancellationToken.None);
        Assert.Equal(["Speaker 1", "Speaker 2"], persisted.Select(entry => entry.Speaker));

        string markdown = await File.ReadAllTextAsync(Path.Combine(
            context.Notes.GetSessionDirectory(result.Session.Id),
            FileSystemNoteRepository.NotesFileName));
        Assert.Contains("**Speaker 1:** first", markdown, StringComparison.Ordinal);
        Assert.Contains("**Speaker 2:** second", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeAsync_CancellationAfterDecodedLine_ReturnsFinalizedPartialSession()
    {
        await using TestContext context = await TestContext.CreateAsync(FakeTranscriberMode.CancelAfterFirst);
        using CancellationTokenSource cancellation = new();
        ImmediateProgress<NoteEntry> entryProgress = new(_ => cancellation.Cancel());

        RecordedMediaTranscriptionResult result = await context.Service.TranscribeAsync(
            context.Request(diarize: false),
            progress: null,
            entryProgress,
            cancellation.Token);

        Assert.True(result.WasCancelled);
        Assert.Single(result.Entries);
        Assert.NotNull(result.Session.EndedUtc);
        Assert.False(File.Exists(context.Converter.OutputPath));

        IReadOnlyList<NoteEntry> persisted = await context.Notes
            .LoadEntriesAsync(result.Session.Id, CancellationToken.None);
        Assert.Single(persisted);
    }

    [Fact]
    public async Task TranscribeAsync_DecodeFault_RethrowsAfterFinalizingPartialSessionAndCleanup()
    {
        await using TestContext context = await TestContext.CreateAsync(FakeTranscriberMode.FaultAfterFirst);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.TranscribeAsync(
                context.Request(diarize: false),
                progress: null,
                entries: null,
                CancellationToken.None));

        Assert.Equal("decode failed", error.Message);
        Assert.False(File.Exists(context.Converter.OutputPath));

        NoteSession session = Assert.Single(await context.Notes.ListSessionsAsync(
            new NoteQuery(),
            CancellationToken.None));
        Assert.NotNull(session.EndedUtc);
        Assert.Single(await context.Notes.LoadEntriesAsync(session.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_NoAudioStreams_DoesNotCreateSession()
    {
        await using TestContext context = await TestContext.CreateAsync(
            FakeTranscriberMode.Success,
            hasAudioStream: false);

        await Assert.ThrowsAsync<MediaConversionException>(() =>
            context.Service.TranscribeAsync(
                context.Request(diarize: false),
                progress: null,
                entries: null,
                CancellationToken.None));

        Assert.Empty(await context.Notes.ListSessionsAsync(new NoteQuery(), CancellationToken.None));
        Assert.False(context.Converter.ExtractCalled);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly string _root;

        private TestContext(
            string root,
            string inputPath,
            FileSystemNoteRepository notes,
            FakeMediaConverter converter,
            RecordedMediaTranscriptionService service)
        {
            _root = root;
            InputPath = inputPath;
            Notes = notes;
            Converter = converter;
            Service = service;
        }

        public string InputPath { get; }
        public FileSystemNoteRepository Notes { get; }
        public FakeMediaConverter Converter { get; }
        public RecordedMediaTranscriptionService Service { get; }

        public static async Task<TestContext> CreateAsync(
            FakeTranscriberMode mode,
            bool hasAudioStream = true)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "WhisperNotes.Core.Tests",
                Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);

            string inputPath = Path.Combine(root, "meeting.mp4");
            await File.WriteAllTextAsync(inputPath, "fake media");

            FileSystemNoteRepository notes = new(Path.Combine(root, "notes"));
            FakeMediaConverter converter = new(hasAudioStream);
            RecordedMediaTranscriptionService service = new(
                converter,
                new FakeWavReader(),
                new FakeTranscriberFactory(mode),
                new FakeSpeakerAttributorFactory(),
                notes);

            return new TestContext(root, inputPath, notes, converter, service);
        }

        public RecordedMediaTranscriptionRequest Request(bool diarize) => new(
            InputPath,
            "Imported meeting",
            Project: "Tests",
            Tags: [],
            ModelUsed: "fake",
            new TranscriptionOptions(),
            new DiarizationOptions { Enabled = diarize });

        public async ValueTask DisposeAsync()
        {
            await Notes.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FakeMediaConverter(bool hasAudioStream) : IMediaConverter
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public string? FfmpegPath => "fake-ffmpeg";
        public string? OutputPath { get; private set; }
        public bool ExtractCalled { get; private set; }

        public Task<IReadOnlyList<MediaAudioStream>> ProbeAudioStreamsAsync(
            string inputPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MediaAudioStream> streams = hasAudioStream
                ? [new MediaAudioStream(1, "aac", 2, 48_000, "eng", null)]
                : [];
            return Task.FromResult(streams);
        }

        public async Task<string> ExtractAudioAsync(
            string inputPath,
            string outputWavPath,
            int? streamIndex,
            IProgress<ConversionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractCalled = true;
            OutputPath = outputWavPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputWavPath)!);
            await File.WriteAllTextAsync(outputWavPath, "fake wave", cancellationToken);
            progress?.Report(new ConversionProgress(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)));
            return outputWavPath;
        }
    }

    private sealed class FakeWavReader : IWavReader
    {
        public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadChunksAsync(
            string wavPath,
            int chunkSamples,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            float[] samples = new float[16_000 * 4];
            Array.Fill(samples, 0.1f);
            yield return samples;
        }

        public TimeSpan GetDuration(string wavPath) => TimeSpan.FromSeconds(4);
    }

    private enum FakeTranscriberMode
    {
        Success,
        CancelAfterFirst,
        FaultAfterFirst
    }

    private sealed class FakeTranscriberFactory(FakeTranscriberMode mode) : ITranscriberFactory
    {
        public Task<ITranscriber> CreateAsync(
            TranscriptionOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ITranscriber>(new FakeTranscriber(mode));
        }
    }

    private sealed class FakeTranscriber(FakeTranscriberMode mode) : ITranscriber
    {
        public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
            ReadOnlyMemory<float> pcm16kMono,
            TimeSpan offset,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TranscriptSegment(
                offset,
                offset + TimeSpan.FromSeconds(2),
                "first",
                0.9f,
                "en");

            if (mode == FakeTranscriberMode.CancelAfterFirst)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (mode == FakeTranscriberMode.FaultAfterFirst)
            {
                throw new InvalidOperationException("decode failed");
            }

            yield return new TranscriptSegment(
                offset + TimeSpan.FromSeconds(2),
                offset + TimeSpan.FromSeconds(4),
                "second",
                0.9f,
                "en");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSpeakerAttributorFactory : ISpeakerAttributorFactory
    {
        public Task<ISpeakerAttributor> CreateAsync(
            DiarizationOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ISpeakerAttributor>(new FakeSpeakerAttributor());
        }
    }

    private sealed class FakeSpeakerAttributor : ISpeakerAttributor
    {
        public bool IsAvailable => true;
        public int Observed { get; private set; }

        public void Observe(
            TimeSpan start,
            TimeSpan end,
            ReadOnlySpan<float> audio,
            TimeSpan audioOffset) => Observed++;

        public SpeakerTimeline Build() => new(
            [
                new SpeakerTurn(TimeSpan.Zero, TimeSpan.FromSeconds(2), 0),
                new SpeakerTurn(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), 1),
            ],
            speakerCount: 2);

        public void Dispose()
        {
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

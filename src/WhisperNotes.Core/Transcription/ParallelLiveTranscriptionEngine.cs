using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WhisperNotes.Core.Transcription;

/// <summary>
/// Coordinates one independent <see cref="ILiveTranscriptionEngine"/> per input and merges their
/// output. A decoder is intentionally not shared: Whisper transcribers are stateful native objects
/// and concurrent calls against one instance are not safe.
/// </summary>
public sealed class ParallelLiveTranscriptionEngine : IParallelLiveTranscriptionEngine
{
    private readonly Func<ILiveTranscriptionEngine> _engineFactory;

    /// <summary>
    /// Creates a coordinator whose inputs each receive a separately loaded transcriber.
    /// </summary>
    public ParallelLiveTranscriptionEngine(
        ITranscriberFactory transcriberFactory,
        ChunkingOptions? chunking = null)
    {
        ArgumentNullException.ThrowIfNull(transcriberFactory);
        _engineFactory = () => new LiveTranscriptionEngine(transcriberFactory, chunking);
    }

    /// <summary>Test seam for exercising coordination independently of audio chunking.</summary>
    internal ParallelLiveTranscriptionEngine(Func<ILiveTranscriptionEngine> engineFactory)
    {
        ArgumentNullException.ThrowIfNull(engineFactory);
        _engineFactory = engineFactory;
    }

    public async IAsyncEnumerable<SourcedTranscriptSegment> RunAsync(
        IReadOnlyCollection<LiveTranscriptionInput> inputs,
        TranscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);

        LiveTranscriptionInput[] activeInputs = ValidateAndSnapshot(inputs);
        if (activeInputs.Length == 0)
        {
            yield break;
        }

        Channel<SourcedTranscriptSegment> results =
            Channel.CreateUnbounded<SourcedTranscriptSegment>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = activeInputs.Length == 1,
                AllowSynchronousContinuations = false
            });

        using CancellationTokenSource sessionCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Exception? firstFailure = null;
        Task[] producers = activeInputs
            .Select(input => Task.Run(
                () => RunInputAsync(input, options, results.Writer, sessionCts, RecordFailure),
                CancellationToken.None))
            .ToArray();

        Task completion = CompleteResultsAsync(producers, results.Writer, () => firstFailure);

        try
        {
            // Do not pass the caller token here. Each live engine observes the linked token and
            // flushes captured audio before it completes, so those final segments must remain visible.
            await foreach (SourcedTranscriptSegment result in results.Reader
                .ReadAllAsync(CancellationToken.None)
                .ConfigureAwait(false))
            {
                yield return result;
            }
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            await completion.ConfigureAwait(false);
        }

        void RecordFailure(Exception failure)
        {
            Interlocked.CompareExchange(ref firstFailure, failure, null);
        }
    }

    private async Task RunInputAsync(
        LiveTranscriptionInput input,
        TranscriptionOptions options,
        ChannelWriter<SourcedTranscriptSegment> writer,
        CancellationTokenSource sessionCts,
        Action<Exception> recordFailure)
    {
        try
        {
            ILiveTranscriptionEngine engine = _engineFactory();
            ArgumentNullException.ThrowIfNull(engine);

            try
            {
                await foreach (TranscriptSegment segment in engine
                    .RunAsync(
                        input.CaptureSource,
                        options,
                        sessionCts.Token,
                        input.SpeakerAttributor)
                    .ConfigureAwait(false))
                {
                    writer.TryWrite(new SourcedTranscriptSegment(
                        input.SourceId,
                        input.SourceName,
                        input.CaptureSource.Channel,
                        segment));
                }
            }
            finally
            {
                await DisposeEngineAsync(engine).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (sessionCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            recordFailure(ex);
            await sessionCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task CompleteResultsAsync(
        IEnumerable<Task> producers,
        ChannelWriter<SourcedTranscriptSegment> writer,
        Func<Exception?> getFailure)
    {
        Exception? unexpectedFailure = null;
        try
        {
            await Task.WhenAll(producers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // RunInputAsync normally converts all producer failures into firstFailure. Keep this
            // guard so an unexpected task-level failure still completes the merged stream instead
            // of leaving its consumer blocked forever.
            unexpectedFailure = ex;
        }

        writer.TryComplete(getFailure() ?? unexpectedFailure);
    }

    private static LiveTranscriptionInput[] ValidateAndSnapshot(
        IReadOnlyCollection<LiveTranscriptionInput> inputs)
    {
        LiveTranscriptionInput[] snapshot = inputs.ToArray();
        HashSet<string> sourceIds = new(StringComparer.Ordinal);

        foreach (LiveTranscriptionInput input in snapshot)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.SourceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.SourceName);
            ArgumentNullException.ThrowIfNull(input.CaptureSource);

            if (!sourceIds.Add(input.SourceId))
            {
                throw new ArgumentException(
                    $"Every live input must have a unique source id. Duplicate: '{input.SourceId}'.",
                    nameof(inputs));
            }
        }

        return snapshot;
    }

    private static async ValueTask DisposeEngineAsync(ILiveTranscriptionEngine engine)
    {
        switch (engine)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}

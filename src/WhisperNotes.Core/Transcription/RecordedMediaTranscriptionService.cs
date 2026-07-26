using System.Runtime.ExceptionServices;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Media;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.Core.Transcription;

/// <summary>
/// Reusable recorded-media ingest for interactive and command-line front ends.
/// </summary>
public sealed class RecordedMediaTranscriptionService : IRecordedMediaTranscriptionService
{
    private const int ChunkSamples = AudioFrame.SampleRate * 30;
    private const float SilenceFloor = 0.0005f;

    private readonly IMediaConverter _media;
    private readonly IWavReader _wavReader;
    private readonly ITranscriberFactory _transcribers;
    private readonly ISpeakerAttributorFactory _diarizers;
    private readonly INoteRepository _notes;

    public RecordedMediaTranscriptionService(
        IMediaConverter media,
        IWavReader wavReader,
        ITranscriberFactory transcribers,
        ISpeakerAttributorFactory diarizers,
        INoteRepository notes)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(wavReader);
        ArgumentNullException.ThrowIfNull(transcribers);
        ArgumentNullException.ThrowIfNull(diarizers);
        ArgumentNullException.ThrowIfNull(notes);

        _media = media;
        _wavReader = wavReader;
        _transcribers = transcribers;
        _diarizers = diarizers;
        _notes = notes;
    }

    public async Task<RecordedMediaTranscriptionResult> TranscribeAsync(
        RecordedMediaTranscriptionRequest request,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        IProgress<NoteEntry>? entries,
        CancellationToken cancellationToken)
    {
        TranscriptionPlan plan = await PrepareAsync(request, progress, cancellationToken).ConfigureAwait(false);

        List<NoteEntry> written = [];
        ISpeakerAttributor? attributor = null;
        SpeakerTimeline? timeline = null;
        TimeSpan? mediaDuration = null;
        ExceptionDispatchInfo? failure = null;
        var wasCancelled = false;

        // Assigned by the finally below, which runs on every path that reaches the read.
        NoteSession? finalized;

        try
        {
            await ExtractAsync(
                plan.InputPath,
                plan.WavPath,
                request.StreamIndex,
                progress,
                cancellationToken).ConfigureAwait(false);

            mediaDuration = TryGetDuration(plan.WavPath);
            attributor = await PrepareAttributorAsync(request.Diarization, progress, cancellationToken)
                .ConfigureAwait(false);

            await DecodeAsync(
                plan.Session,
                plan.WavPath,
                request.Transcription,
                attributor,
                written,
                entries,
                progress,
                cancellationToken).ConfigureAwait(false);

            timeline = await AttributeSpeakersAsync(
                plan.Session,
                written,
                attributor,
                progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            wasCancelled = true;
            timeline = await AttributeSpeakersAsync(
                plan.Session,
                written,
                attributor,
                progress).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            (finalized, failure) = await FinalizeAsync(
                plan,
                attributor,
                request.KeepAudio,
                mediaDuration,
                progress,
                failure).ConfigureAwait(false);
        }

        failure?.Throw();

        MirrorSpeakerLabels(written, timeline);

        progress?.Report(new(
            RecordedMediaTranscriptionStage.Completed,
            Fraction: 1,
            Processed: mediaDuration,
            Total: mediaDuration,
            Detail: wasCancelled ? "Partial transcript saved" : "Transcript saved"));

        return new RecordedMediaTranscriptionResult(
            finalized ?? plan.Session,
            written.AsReadOnly(),
            timeline,
            wasCancelled);
    }

    /// <summary>
    /// Everything that has to be settled before a single sample is decoded: that the request and the
    /// media are usable, which session the transcript lands in, and where the extracted WAV goes.
    /// </summary>
    private async Task<TranscriptionPlan> PrepareAsync(
        RecordedMediaTranscriptionRequest request,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_media.IsAvailable)
        {
            throw new MediaConversionException(
                _media.UnavailableReason ?? "ffmpeg and ffprobe are unavailable.");
        }

        string inputPath = Path.GetFullPath(request.InputPath);
        progress?.Report(new(RecordedMediaTranscriptionStage.Probing, Detail: Path.GetFileName(inputPath)));

        IReadOnlyList<MediaAudioStream> streams = await _media
            .ProbeAudioStreamsAsync(inputPath, cancellationToken)
            .ConfigureAwait(false);

        if (streams.Count == 0)
        {
            throw new MediaConversionException(
                $"{Path.GetFileName(inputPath)} contains no audio streams.");
        }

        ValidateStream(request.StreamIndex, streams, inputPath);

        NoteSession session = await _notes.CreateSessionAsync(
            request.Title.Trim(),
            Normalize(request.Project),
            SourceDescription(inputPath, request.StreamIndex),
            CleanTags(request.Tags),
            Normalize(request.ModelUsed),
            cancellationToken).ConfigureAwait(false);

        string wavPath = request.KeepAudio
            ? Path.Combine(
                _notes.GetSessionDirectory(session.Id),
                FileSystemNoteRepository.AudioDirectoryName,
                "session.wav")
            : Path.Combine(Path.GetTempPath(), $"whispernotes-{session.Id}.wav");

        return new TranscriptionPlan(inputPath, session, wavPath);
    }

    /// <summary>
    /// Tears the run down and closes the session, whether it completed, was cancelled or faulted.
    /// </summary>
    /// <param name="failure">The pipeline fault so far, or null; returned possibly replaced.</param>
    /// <returns>The closed session, and whichever failure the caller should ultimately throw.</returns>
    private async Task<(NoteSession? Finalized, ExceptionDispatchInfo? Failure)> FinalizeAsync(
        TranscriptionPlan plan,
        ISpeakerAttributor? attributor,
        bool keepAudio,
        TimeSpan? mediaDuration,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        ExceptionDispatchInfo? failure)
    {
        TryDispose(attributor);

        if (!keepAudio)
        {
            TryDelete(plan.WavPath);
        }

        NoteSession? finalized = null;
        try
        {
            progress?.Report(new(RecordedMediaTranscriptionStage.Finalizing));
            finalized = await _notes
                .FinalizeSessionAsync(plan.Session.Id, CancellationToken.None, mediaDuration)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (failure is not null)
        {
            // Keep the pipeline fault as the primary error while retaining the finalization
            // failure for diagnostics.
            failure.SourceException.Data["WhisperNotes.FinalizationFailure"] = ex;
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        return (finalized, failure);
    }

    /// <summary>
    /// SpeakerAttribution updates durable entries. Mirror those labels into the returned list so UI
    /// callers do not need an immediate disk round-trip just to refresh speaker chips.
    /// </summary>
    private static void MirrorSpeakerLabels(List<NoteEntry> written, SpeakerTimeline? timeline)
    {
        if (timeline is not { WorthLabelling: true })
        {
            return;
        }

        for (var i = 0; i < written.Count; i++)
        {
            NoteEntry entry = written[i];
            if (timeline.Label(entry.Offset, entry.EndOffset ?? entry.Offset) is { } speaker)
            {
                written[i] = entry with { Speaker = speaker };
            }
        }
    }

    private async Task ExtractAsync(
        string inputPath,
        string wavPath,
        int? streamIndex,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        IProgress<ConversionProgress>? conversion = progress is null
            ? null
            : new ConversionProgressAdapter(progress);

        progress?.Report(new(RecordedMediaTranscriptionStage.Extracting, Fraction: 0));
        await _media
            .ExtractAudioAsync(inputPath, wavPath, streamIndex, conversion, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ISpeakerAttributor?> PrepareAttributorAsync(
        DiarizationOptions options,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return null;
        }

        progress?.Report(new(
            RecordedMediaTranscriptionStage.PreparingSpeakers,
            Detail: $"Up to {options.MaxSpeakers} anonymous speakers"));

        try
        {
            return await _diarizers.CreateAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Speaker labels are enrichment. Model download/load failure must never cost the words.
            return null;
        }
    }

    private async Task DecodeAsync(
        NoteSession session,
        string wavPath,
        TranscriptionOptions options,
        ISpeakerAttributor? attributor,
        List<NoteEntry> written,
        IProgress<NoteEntry>? entries,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        TimeSpan total = _wavReader.GetDuration(wavPath);
        await using ITranscriber transcriber = await _transcribers
            .CreateAsync(options, cancellationToken)
            .ConfigureAwait(false);

        var offset = TimeSpan.Zero;
        progress?.Report(new(
            RecordedMediaTranscriptionStage.Transcribing,
            Fraction: total > TimeSpan.Zero ? 0 : null,
            Processed: offset,
            Total: total));

        await foreach (ReadOnlyMemory<float> chunk in _wavReader
                           .ReadChunksAsync(wavPath, ChunkSamples, cancellationToken)
                           .ConfigureAwait(false))
        {
            TimeSpan duration = TimeSpan.FromSeconds((double)chunk.Length / AudioFrame.SampleRate);

            if (HasSignal(chunk.Span))
            {
                await foreach (TranscriptSegment segment in transcriber
                                   .TranscribeAsync(chunk, offset, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(segment.Text))
                    {
                        continue;
                    }

                    NoteEntry entry = ToEntry(session, segment);

                    // Once decoded, a line must reach disk even if cancellation arrives while it
                    // is being committed.
                    await _notes
                        .AppendEntryAsync(session.Id, entry, CancellationToken.None)
                        .ConfigureAwait(false);

                    written.Add(entry);
                    TryObserve(attributor, segment, chunk.Span, offset);
                    entries?.Report(entry);
                }
            }

            offset += duration;
            progress?.Report(new(
                RecordedMediaTranscriptionStage.Transcribing,
                Fraction: total > TimeSpan.Zero ? Math.Clamp(offset / total, 0, 1) : null,
                Processed: offset,
                Total: total));
        }
    }

    private async Task<SpeakerTimeline?> AttributeSpeakersAsync(
        NoteSession session,
        IReadOnlyList<NoteEntry> entries,
        ISpeakerAttributor? attributor,
        IProgress<RecordedMediaTranscriptionProgress>? progress)
    {
        if (attributor is not { IsAvailable: true } ||
            attributor.Observed == 0 ||
            entries.Count == 0)
        {
            return null;
        }

        progress?.Report(new(
            RecordedMediaTranscriptionStage.Diarizing,
            Detail: $"{attributor.Observed} observations"));

        try
        {
            SpeakerTimeline timeline = await Task.Run(attributor.Build).ConfigureAwait(false);
            if (timeline.WorthLabelling)
            {
                await SpeakerAttribution
                    .ApplyAsync(_notes, session.Id, entries, timeline, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return timeline;
        }
        catch
        {
            // The transcript is complete and useful even when clustering fails.
            return null;
        }
    }

    private TimeSpan? TryGetDuration(string wavPath)
    {
        try
        {
            return _wavReader.GetDuration(wavPath);
        }
        catch
        {
            return null;
        }
    }

    private static NoteEntry ToEntry(NoteSession session, TranscriptSegment segment) => new(
        Guid.CreateVersion7().ToString("n"),
        session.StartedUtc + segment.Start,
        segment.Start,
        NoteEntryKind.Dictation,
        segment.Text.Trim(),
        Speaker: null,
        Confidence: segment.Confidence,
        EndOffset: segment.End);

    private static void Validate(RecordedMediaTranscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentNullException.ThrowIfNull(request.Tags);
        ArgumentNullException.ThrowIfNull(request.Transcription);
        ArgumentNullException.ThrowIfNull(request.Diarization);

        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("Recorded media file not found.", request.InputPath);
        }
    }

    private static void ValidateStream(
        int? requested,
        IReadOnlyList<MediaAudioStream> streams,
        string inputPath)
    {
        if (requested is not { } streamIndex ||
            streams.Any(stream => stream.Index == streamIndex))
        {
            return;
        }

        throw new MediaConversionException(
            $"{Path.GetFileName(inputPath)} has no audio stream #{streamIndex}.");
    }

    private static string SourceDescription(string inputPath, int? streamIndex) =>
        $"video: {Path.GetFileName(inputPath)}" +
        (streamIndex is { } index ? $" (stream #{index})" : string.Empty);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] CleanTags(IReadOnlyList<string> tags) =>
        [.. tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)).Select(static tag => tag.Trim())];

    private static bool HasSignal(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float sample in samples)
        {
            sum += (double)sample * sample;
        }

        return samples.Length > 0 && Math.Sqrt(sum / samples.Length) >= SilenceFloor;
    }

    private static void TryObserve(
        ISpeakerAttributor? attributor,
        TranscriptSegment segment,
        ReadOnlySpan<float> audio,
        TimeSpan audioOffset)
    {
        try
        {
            attributor?.Observe(segment.Start, segment.End, audio, audioOffset);
        }
        catch
        {
            // Voice-printing is enrichment. A bad observation cannot be allowed to discard words
            // that were already decoded and committed to the transcript.
        }
    }

    private static void TryDispose(ISpeakerAttributor? attributor)
    {
        try
        {
            attributor?.Dispose();
        }
        catch
        {
            // Teardown of optional diarization must not prevent WAV cleanup or session finalization.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup failure is non-fatal: the durable transcript matters more than a leaked WAV.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure is non-fatal: the durable transcript matters more than a leaked WAV.
        }
    }

    /// <summary>What to read, where the session lives, and where the extracted audio goes.</summary>
    private readonly record struct TranscriptionPlan(string InputPath, NoteSession Session, string WavPath);

    private sealed class ConversionProgressAdapter(
        IProgress<RecordedMediaTranscriptionProgress> progress) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => progress.Report(new(
            RecordedMediaTranscriptionStage.Extracting,
            value.Fraction,
            value.Processed,
            value.Total));
    }
}

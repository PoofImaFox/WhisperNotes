using WhisperNotes.Core.Media;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.DesignData;

/// <summary>Reports ffmpeg as present so the status bar shows the healthy path.</summary>
internal sealed class FakeMediaConverter : IMediaConverter
{
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public string? FfmpegPath => @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";

    public Task<IReadOnlyList<MediaAudioStream>> ProbeAudioStreamsAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaAudioStream> streams =
        [
            new(1, "aac", 2, 48_000, "eng", "Meeting audio"),
            new(2, "aac", 1, 48_000, "eng", "Presenter mic"),
        ];

        return Task.FromResult(streams);
    }

    public async Task<string> ExtractAudioAsync(
        string inputPath,
        string outputWavPath,
        int? streamIndex,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = TimeSpan.FromMinutes(41);
        for (var i = 1; i <= 20; i++)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ConversionProgress(total * i / 20.0, total));
        }

        return outputWavPath;
    }
}

/// <summary>Fast, deterministic recorded-media ingest for the sample-data shell.</summary>
internal sealed class FakeRecordedMediaTranscriptionService(INoteRepository notes)
    : IRecordedMediaTranscriptionService
{
    public async Task<RecordedMediaTranscriptionResult> TranscribeAsync(
        RecordedMediaTranscriptionRequest request,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        IProgress<NoteEntry>? entries,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(RecordedMediaTranscriptionStage.Probing, Detail: Path.GetFileName(request.InputPath)));
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        NoteSession session = await notes.CreateSessionAsync(
            request.Title,
            request.Project,
            $"video: {Path.GetFileName(request.InputPath)}",
            request.Tags,
            request.ModelUsed,
            cancellationToken).ConfigureAwait(false);

        List<NoteEntry> written = [];
        var wasCancelled = false;
        try
        {
            for (var i = 0; i < 9; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(90, cancellationToken).ConfigureAwait(false);

                var start = TimeSpan.FromSeconds(i * 4);
                var speaker = (i / 3) % 3 == 1 ? "Speaker 2" : "Speaker 1";
                NoteEntry entry = new(
                    Guid.CreateVersion7().ToString("n"),
                    session.StartedUtc + start,
                    start,
                    NoteEntryKind.Dictation,
                    SampleData.DictationScript[i % SampleData.DictationScript.Count],
                    speaker,
                    0.88f,
                    start + TimeSpan.FromSeconds(3));

                await notes.AppendEntryAsync(session.Id, entry, CancellationToken.None).ConfigureAwait(false);
                written.Add(entry);
                entries?.Report(entry);
                progress?.Report(new(
                    RecordedMediaTranscriptionStage.Transcribing,
                    Fraction: (i + 1d) / 9,
                    Processed: start + TimeSpan.FromSeconds(3),
                    Total: TimeSpan.FromSeconds(36)));
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }

        NoteSession finalized = await notes
            .FinalizeSessionAsync(session.Id, CancellationToken.None, TimeSpan.FromSeconds(36))
            .ConfigureAwait(false);

        progress?.Report(new(RecordedMediaTranscriptionStage.Completed, Fraction: 1));
        return new RecordedMediaTranscriptionResult(finalized, written, Speakers: null, WasCancelled: wasCancelled);
    }
}

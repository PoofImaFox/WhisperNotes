using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.Core.Transcription;

/// <summary>The current phase of a recorded-media transcription.</summary>
public enum RecordedMediaTranscriptionStage
{
    Probing,
    Extracting,
    PreparingSpeakers,
    Transcribing,
    Diarizing,
    Finalizing,
    Completed
}

/// <summary>Everything needed to turn one recorded media file into a persisted note session.</summary>
public sealed record RecordedMediaTranscriptionRequest(
    string InputPath,
    string Title,
    string? Project,
    IReadOnlyList<string> Tags,
    string? ModelUsed,
    TranscriptionOptions Transcription,
    DiarizationOptions Diarization,
    int? StreamIndex = null,
    bool KeepAudio = false);

/// <summary>
/// Progress shared by extraction and decoding. <see cref="Fraction"/> is null when the total
/// duration is not known.
/// </summary>
public readonly record struct RecordedMediaTranscriptionProgress(
    RecordedMediaTranscriptionStage Stage,
    double? Fraction = null,
    TimeSpan? Processed = null,
    TimeSpan? Total = null,
    string? Detail = null);

/// <summary>The durable outcome of a recorded-media transcription.</summary>
/// <param name="Session">The finalized session, including the media-derived duration.</param>
/// <param name="Entries">Every line persisted before completion or cancellation.</param>
/// <param name="Speakers">The anonymous speaker timeline, when diarization produced one.</param>
/// <param name="WasCancelled">
/// True when cancellation stopped extraction or decoding after the session had been created.
/// The partial session and all decoded lines are still finalized and returned.
/// </param>
public sealed record RecordedMediaTranscriptionResult(
    NoteSession Session,
    IReadOnlyList<NoteEntry> Entries,
    SpeakerTimeline? Speakers,
    bool WasCancelled);

/// <summary>
/// Converts and transcribes a recorded audio/video file into the same crash-safe session format
/// used by live capture.
/// </summary>
public interface IRecordedMediaTranscriptionService
{
    /// <summary>
    /// Runs the complete ingest. Cancellation before a session exists throws normally; cancellation
    /// after creation returns a finalized partial result with <c>WasCancelled = true</c>. A decoded
    /// line is reported through <paramref name="entries"/> only after it is safely on disk.
    /// </summary>
    Task<RecordedMediaTranscriptionResult> TranscribeAsync(
        RecordedMediaTranscriptionRequest request,
        IProgress<RecordedMediaTranscriptionProgress>? progress,
        IProgress<NoteEntry>? entries,
        CancellationToken cancellationToken);
}

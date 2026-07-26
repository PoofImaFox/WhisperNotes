using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;

namespace WhisperNotes.Core.Transcription;

/// <summary>
/// One configured input participating in a parallel live transcription session.
/// </summary>
/// <param name="SourceId">
/// Stable configuration identity. This is deliberately independent of the physical
/// <see cref="AudioChannel.Id"/> so changing devices does not rewrite transcript provenance.
/// </param>
/// <param name="SourceName">User-facing input label, for example "Meeting audio" or "My microphone".</param>
/// <param name="CaptureSource">The live audio source opened for this run.</param>
/// <param name="SpeakerAttributor">
/// Optional source-local speaker observer. Attributors cannot be shared across inputs because
/// their timelines and captured PCM belong to different devices.
/// </param>
/// <remarks>
/// The caller retains ownership of <see cref="CaptureSource"/> and <see cref="SpeakerAttributor"/>
/// and must dispose them after enumeration completes.
/// </remarks>
public sealed record LiveTranscriptionInput(
    string SourceId,
    string SourceName,
    IAudioCaptureSource CaptureSource,
    ISpeakerAttributor? SpeakerAttributor = null);

/// <summary>A decoded segment tagged with the configured input that produced it.</summary>
/// <param name="SourceId">Stable configured input identity.</param>
/// <param name="SourceName">Configured input label as it was when the session began.</param>
/// <param name="Channel">Physical endpoint used for this session.</param>
/// <param name="Segment">Decoded source-local segment.</param>
public sealed record SourcedTranscriptSegment(
    string SourceId,
    string SourceName,
    AudioChannel Channel,
    TranscriptSegment Segment);

/// <summary>
/// Captures and transcribes multiple inputs concurrently, merging their independently decoded
/// segments into one provenance-tagged stream.
/// </summary>
public interface IParallelLiveTranscriptionEngine
{
    /// <summary>
    /// Runs every supplied input concurrently. Cancellation stops all inputs but retains the
    /// normal live-engine behavior of flushing audio that was already captured. If one input
    /// fails, the remaining inputs are cancelled and the originating failure is surfaced after
    /// already-produced results have been read. Results from different sources are emitted in
    /// decode-completion order; use <see cref="TranscriptSegment.Start"/> when a downstream view
    /// needs to sort them by source-local capture time.
    /// </summary>
    IAsyncEnumerable<SourcedTranscriptSegment> RunAsync(
        IReadOnlyCollection<LiveTranscriptionInput> inputs,
        TranscriptionOptions options,
        CancellationToken cancellationToken);
}

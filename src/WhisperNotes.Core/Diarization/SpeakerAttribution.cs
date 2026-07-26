using WhisperNotes.Core.Notes;

namespace WhisperNotes.Core.Diarization;

/// <summary>
/// Writes a finished <see cref="SpeakerTimeline"/> back onto the lines it describes.
/// </summary>
/// <remarks>
/// This runs after transcription rather than during it, because who the second speaker is cannot be
/// known until the recording has been heard to the end. The lines are already safely on disk by
/// then; stamping a speaker onto them is an edit, which the repository records as a new revision of
/// the same entry rather than as a rewrite of the log.
/// </remarks>
public static class SpeakerAttribution
{
    /// <summary>
    /// Labels every dictation line the timeline can account for, and returns how many it labelled.
    /// </summary>
    /// <param name="notes">Repository holding the session.</param>
    /// <param name="sessionId">Session being attributed.</param>
    /// <param name="entries">The lines as they were written, in order.</param>
    /// <param name="timeline">What the diarizer concluded.</param>
    /// <param name="cancellationToken">
    /// Callers should generally pass <see cref="CancellationToken.None"/>: interrupting halfway
    /// leaves a transcript where some lines name a speaker and others do not, which reads as a
    /// diarization failure rather than as a cancelled command.
    /// </param>
    public static async Task<int> ApplyAsync(
        INoteRepository notes,
        string sessionId,
        IEnumerable<NoteEntry> entries,
        SpeakerTimeline timeline,
        CancellationToken cancellationToken,
        ISpeakerProfileStore? profiles = null,
        double profileMatchThreshold = 0.35)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(timeline);

        if (entries is null)
        {
            return 0;
        }

        await IdentifyProfilesAsync(timeline, profiles, profileMatchThreshold, cancellationToken)
            .ConfigureAwait(false);

        if (!timeline.WorthLabelling && !timeline.HasNamedProfiles)
        {
            return 0;
        }

        var labelled = 0;

        foreach (NoteEntry entry in entries)
        {
            // Only dictation gets attributed. A typed note was written by whoever is running the
            // app, and a marker belongs to the recording rather than to anyone in it.
            if (entry is null ||
                entry.Kind != NoteEntryKind.Dictation ||
                !string.IsNullOrWhiteSpace(entry.Speaker))
            {
                continue;
            }

            var label = timeline.Label(entry.Offset, entry.EndOffset ?? entry.Offset);
            if (label is null)
            {
                continue;
            }

            SpeakerVoiceProfile? profile = timeline.Profile(
                entry.Offset,
                entry.EndOffset ?? entry.Offset);

            await notes.UpdateEntryAsync(
                    sessionId,
                    entry with
                    {
                        Speaker = label,
                        SpeakerProfileId = profile?.Id,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            labelled++;
        }

        return labelled;
    }

    /// <summary>
    /// Resolves every cluster voiceprint against the durable local catalog. Pipelines that add
    /// source-qualified labels can call this without delegating their label formatting.
    /// </summary>
    public static async Task IdentifyProfilesAsync(
        SpeakerTimeline timeline,
        ISpeakerProfileStore? profiles,
        double matchThreshold,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        if (profiles is null)
        {
            return;
        }

        for (var speaker = 0; speaker < timeline.SpeakerCount; speaker++)
        {
            if (!timeline.TryGetVoicePrint(speaker, out ReadOnlyMemory<float> voicePrint))
            {
                continue;
            }

            try
            {
                SpeakerVoiceProfile profile = await profiles
                    .IdentifyAsync(voicePrint, matchThreshold, cancellationToken)
                    .ConfigureAwait(false);

                timeline.SetProfile(speaker, profile);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Durable identity is enrichment on top of diarization. A damaged or unwritable
                // profile file must not cost the session-local "Speaker N" labels.
            }
        }
    }
}

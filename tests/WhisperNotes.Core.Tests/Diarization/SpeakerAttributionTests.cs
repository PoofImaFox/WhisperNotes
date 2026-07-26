using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.Core.Tests.Diarization;

public sealed class SpeakerAttributionTests
{
    [Fact]
    public async Task ApplyAsync_Persists_A_B_A_AndRendersReturningSpeaker()
    {
        var root = Path.Combine(Path.GetTempPath(), "WhisperNotes.Core.Tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            await using FileSystemNoteRepository notes = new(root);
            NoteSession session = await notes.CreateSessionAsync(
                "Speaker test",
                project: null,
                "test audio",
                tags: [],
                modelUsed: "test",
                CancellationToken.None);

            NoteEntry[] entries =
            [
                Entry("a", 0, 1, "Opening"),
                Entry("b", 2, 3, "Reply"),
                Entry("c", 4, 5, "Back again"),
            ];

            foreach (NoteEntry entry in entries)
            {
                await notes.AppendEntryAsync(session.Id, entry, CancellationToken.None);
            }

            SpeakerTimeline timeline = new(
                [
                    new SpeakerTurn(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1), 0),
                    new SpeakerTurn(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), 1),
                    new SpeakerTurn(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), 0),
                ],
                speakerCount: 2);

            int labelled = await SpeakerAttribution.ApplyAsync(
                notes,
                session.Id,
                entries,
                timeline,
                CancellationToken.None);

            Assert.Equal(3, labelled);

            IReadOnlyList<NoteEntry> loaded = await notes.LoadEntriesAsync(session.Id, CancellationToken.None);
            Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 1"], loaded.Select(entry => entry.Speaker));

            await notes.FinalizeSessionAsync(session.Id, CancellationToken.None);

            string sessionDirectory = notes.GetSessionDirectory(session.Id);
            string transcriptPath = Path.Combine(sessionDirectory, FileSystemNoteRepository.TranscriptFileName);
            Assert.Equal(6, (await File.ReadAllLinesAsync(transcriptPath)).Length);

            string markdown = await File.ReadAllTextAsync(Path.Combine(
                sessionDirectory,
                FileSystemNoteRepository.NotesFileName));

            int first = markdown.IndexOf("**Speaker 1:** Opening", StringComparison.Ordinal);
            int second = markdown.IndexOf("**Speaker 2:** Reply", StringComparison.Ordinal);
            int returned = markdown.IndexOf("**Speaker 1:** Back again", StringComparison.Ordinal);

            Assert.True(first >= 0);
            Assert.True(second > first);
            Assert.True(returned > second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static NoteEntry Entry(string id, double start, double end, string text) => new(
        id,
        DateTimeOffset.UtcNow,
        TimeSpan.FromSeconds(start),
        NoteEntryKind.Dictation,
        text,
        Speaker: null,
        Confidence: 0.9f,
        EndOffset: TimeSpan.FromSeconds(end));
}

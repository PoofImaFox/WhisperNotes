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

    [Fact]
    public async Task ApplyAsync_UsesStoredNameAndPersistsExactProfileId()
    {
        var root = Path.Combine(Path.GetTempPath(), "WhisperNotes.Core.Tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            await using FileSystemNoteRepository notes = new(root);
            using var profiles = new JsonSpeakerProfileStore(Path.Combine(root, "speaker-profiles.json"));

            SpeakerVoiceProfile alex = await profiles.IdentifyAsync(
                new float[] { 1, 0 },
                matchThreshold: 0.1,
                CancellationToken.None);
            await profiles.RenameAsync(alex.Id, "Alex", CancellationToken.None);

            NoteSession session = await notes.CreateSessionAsync(
                "Known speaker",
                project: null,
                "test audio",
                tags: [],
                modelUsed: "test",
                CancellationToken.None);

            NoteEntry[] entries =
            [
                Entry("a", 0, 1, "Known voice"),
                Entry("b", 2, 3, "New voice"),
            ];

            foreach (NoteEntry entry in entries)
            {
                await notes.AppendEntryAsync(session.Id, entry, CancellationToken.None);
            }

            SpeakerTimeline timeline = new(
                [
                    new SpeakerTurn(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0),
                    new SpeakerTurn(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), 1),
                ],
                speakerCount: 2,
                voicePrints:
                [
                    [0.999f, 0.02f],
                    [0, 1],
                ]);

            int labelled = await SpeakerAttribution.ApplyAsync(
                notes,
                session.Id,
                entries,
                timeline,
                CancellationToken.None,
                profiles,
                profileMatchThreshold: 0.1);

            Assert.Equal(2, labelled);
            IReadOnlyList<NoteEntry> loaded =
                await notes.LoadEntriesAsync(session.Id, CancellationToken.None);

            Assert.Equal("Alex", loaded[0].Speaker);
            Assert.Equal(alex.Id, loaded[0].SpeakerProfileId);
            Assert.Equal("Speaker 2", loaded[1].Speaker);
            Assert.False(string.IsNullOrWhiteSpace(loaded[1].SpeakerProfileId));
            Assert.NotEqual(alex.Id, loaded[1].SpeakerProfileId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_LabelsKnownSingleSpeakerWithoutAddingAnonymousClutter()
    {
        var root = Path.Combine(Path.GetTempPath(), "WhisperNotes.Core.Tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            await using FileSystemNoteRepository notes = new(root);
            using var profiles = new JsonSpeakerProfileStore(Path.Combine(root, "speaker-profiles.json"));

            SpeakerVoiceProfile alex = await profiles.IdentifyAsync(
                new float[] { 1, 0 },
                matchThreshold: 0.1,
                CancellationToken.None);
            await profiles.RenameAsync(alex.Id, "Alex", CancellationToken.None);

            NoteSession session = await notes.CreateSessionAsync(
                "Known dictation",
                project: null,
                "test audio",
                tags: [],
                modelUsed: "test",
                CancellationToken.None);
            NoteEntry entry = Entry("a", 0, 1, "Only voice");
            await notes.AppendEntryAsync(session.Id, entry, CancellationToken.None);

            SpeakerTimeline timeline = new(
                [new SpeakerTurn(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0)],
                speakerCount: 1,
                voicePrints: [[0.999f, 0.02f]]);

            int labelled = await SpeakerAttribution.ApplyAsync(
                notes,
                session.Id,
                [entry],
                timeline,
                CancellationToken.None,
                profiles,
                profileMatchThreshold: 0.1);

            Assert.Equal(1, labelled);
            NoteEntry loaded = Assert.Single(
                await notes.LoadEntriesAsync(session.Id, CancellationToken.None));
            Assert.Equal("Alex", loaded.Speaker);
            Assert.Equal(alex.Id, loaded.SpeakerProfileId);
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

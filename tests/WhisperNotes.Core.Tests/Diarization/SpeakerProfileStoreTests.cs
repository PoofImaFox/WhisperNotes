using WhisperNotes.Core.Diarization;

namespace WhisperNotes.Core.Tests.Diarization;

public sealed class SpeakerProfileStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "WhisperNotes.Core.Tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task IdentifyAsync_ReusesClosestStoredProfileAcrossInstances()
    {
        string path = ProfilesPath();
        string originalId;

        using (var store = new JsonSpeakerProfileStore(path))
        {
            SpeakerVoiceProfile original = await store.IdentifyAsync(
                new float[] { 1, 0, 0 },
                matchThreshold: 0.2,
                CancellationToken.None);
            originalId = original.Id;
            await store.RenameAsync(original.Id, "Alex", CancellationToken.None);
        }

        using var reloaded = new JsonSpeakerProfileStore(path);
        SpeakerVoiceProfile matched = await reloaded.IdentifyAsync(
            new float[] { 0.999f, 0.02f, 0 },
            matchThreshold: 0.2,
            CancellationToken.None);

        Assert.Equal(originalId, matched.Id);
        Assert.Equal("Alex", matched.Name);
        Assert.Single(reloaded.Load());
    }

    [Fact]
    public async Task RenameAsync_AllowsDistinctVoiceprintsToShareOneName()
    {
        using var store = new JsonSpeakerProfileStore(ProfilesPath());
        SpeakerVoiceProfile opening = await store.IdentifyAsync(
            new float[] { 1, 0 },
            matchThreshold: 0.1,
            CancellationToken.None);
        SpeakerVoiceProfile falseSplit = await store.IdentifyAsync(
            new float[] { 0, 1 },
            matchThreshold: 0.1,
            CancellationToken.None);

        await store.RenameAsync(opening.Id, "Alex", CancellationToken.None);
        await store.RenameAsync(falseSplit.Id, "Alex", CancellationToken.None);

        IReadOnlyList<SpeakerVoiceProfile> stored = store.Load();
        Assert.Equal(2, stored.Count);
        Assert.Equal(2, stored.Select(profile => profile.Id).Distinct().Count());
        Assert.All(stored, profile => Assert.Equal("Alex", profile.Name));

        SpeakerVoiceProfile openingAgain = await store.IdentifyAsync(
            new float[] { 1, 0 },
            matchThreshold: 0.1,
            CancellationToken.None);
        SpeakerVoiceProfile falseSplitAgain = await store.IdentifyAsync(
            new float[] { 0, 1 },
            matchThreshold: 0.1,
            CancellationToken.None);

        Assert.Equal(opening.Id, openingAgain.Id);
        Assert.Equal(falseSplit.Id, falseSplitAgain.Id);
        Assert.Equal("Alex", openingAgain.Name);
        Assert.Equal("Alex", falseSplitAgain.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string ProfilesPath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "speaker-profiles.json");
    }
}

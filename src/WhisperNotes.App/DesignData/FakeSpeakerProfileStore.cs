using WhisperNotes.Core.Diarization;

namespace WhisperNotes.App.DesignData;

/// <summary>Small in-memory catalog for the designer and sample-data fallback.</summary>
internal sealed class FakeSpeakerProfileStore : ISpeakerProfileStore
{
    private readonly List<SpeakerVoiceProfile> _profiles =
    [
        Profile("sample-anna", "Anna", [1, 0, 0]),
        Profile("sample-dave", "Dave", [0, 1, 0]),
        // Deliberately the same name: the settings preview demonstrates that false splits retain
        // both acoustic profiles instead of destructively averaging them.
        Profile("sample-dave-headset", "Dave", [0, 0.85f, 0.15f]),
    ];

    public string ProfilesPath => "in-memory sample profiles";

    public IReadOnlyList<SpeakerVoiceProfile> Load() =>
        _profiles.Select(profile => profile with { VoicePrint = [.. profile.VoicePrint] }).ToArray();

    public Task<SpeakerVoiceProfile> IdentifyAsync(
        ReadOnlyMemory<float> voicePrint,
        double matchThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SpeakerVoiceProfile profile = Profile(
            $"sample-{_profiles.Count + 1}",
            null,
            voicePrint.ToArray());
        _profiles.Add(profile);
        return Task.FromResult(profile);
    }

    public Task<SpeakerVoiceProfile> RenameAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int index = _profiles.FindIndex(profile =>
            string.Equals(profile.Id, profileId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new KeyNotFoundException($"Speaker profile '{profileId}' does not exist.");
        }

        SpeakerVoiceProfile renamed = _profiles[index] with
        {
            Name = name.Trim(),
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        _profiles[index] = renamed;
        return Task.FromResult(renamed);
    }

    private static SpeakerVoiceProfile Profile(string id, string? name, float[] voicePrint)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(id, name, voicePrint, now, now);
    }
}

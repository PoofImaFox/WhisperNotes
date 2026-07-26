using System.Text;
using System.Text.Json;
using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.Core.Diarization;

/// <summary>
/// One durable acoustic identity. <see cref="Name"/> is intentionally not unique: diarization can
/// split one person into two sufficiently different voiceprints, and naming both profiles "Alex"
/// is safer than throwing either voiceprint away.
/// </summary>
public sealed record SpeakerVoiceProfile(
    string Id,
    string? Name,
    float[] VoicePrint,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Persists voiceprints and resolves newly detected voices against them.</summary>
public interface ISpeakerProfileStore
{
    /// <summary>Location of the profile JSON, primarily for diagnostics and settings surfaces.</summary>
    string ProfilesPath { get; }

    /// <summary>Returns a snapshot ordered by when each profile was first stored.</summary>
    IReadOnlyList<SpeakerVoiceProfile> Load();

    /// <summary>
    /// Finds the closest stored voice within <paramref name="matchThreshold"/>, or stores a new
    /// anonymous profile. A new profile is retained even before it is named so a later transcript
    /// rename can assign its stable id.
    /// </summary>
    Task<SpeakerVoiceProfile> IdentifyAsync(
        ReadOnlyMemory<float> voicePrint,
        double matchThreshold,
        CancellationToken cancellationToken);

    /// <summary>
    /// Assigns a display name to exactly one acoustic profile. Giving two ids the same name does
    /// not merge them; both remain available to match future detections.
    /// </summary>
    Task<SpeakerVoiceProfile> RenameAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken);
}

/// <summary>
/// Crash-safe JSON persistence for speaker voice profiles. Profiles live beside settings rather
/// than notes so recognition follows the user across every project and session.
/// </summary>
public sealed class JsonSpeakerProfileStore : ISpeakerProfileStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSpeakerProfileStore(string? profilesPath = null)
    {
        ProfilesPath = string.IsNullOrWhiteSpace(profilesPath)
            ? AppSettings.DefaultSpeakerProfilesPath
            : Path.GetFullPath(profilesPath);
    }

    public string ProfilesPath { get; }

    public IReadOnlyList<SpeakerVoiceProfile> Load() =>
        LoadCore()
            .OrderBy(profile => profile.CreatedUtc)
            .Select(Clone)
            .ToArray();

    public async Task<SpeakerVoiceProfile> IdentifyAsync(
        ReadOnlyMemory<float> voicePrint,
        double matchThreshold,
        CancellationToken cancellationToken)
    {
        float[] normalized = Normalize(voicePrint.Span);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A voiceprint must contain at least one finite, non-zero value.", nameof(voicePrint));
        }

        if (matchThreshold is < 0 or > 2 || double.IsNaN(matchThreshold))
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchThreshold),
                "A cosine-distance threshold must be between 0 and 2.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<SpeakerVoiceProfile> profiles = LoadCore();
            SpeakerVoiceProfile? closest = null;
            var closestDistance = double.MaxValue;

            foreach (SpeakerVoiceProfile profile in profiles)
            {
                if (profile.VoicePrint.Length != normalized.Length)
                {
                    continue;
                }

                double distance = CosineDistance(normalized, profile.VoicePrint);
                if (distance <= matchThreshold && distance < closestDistance)
                {
                    closest = profile;
                    closestDistance = distance;
                }
            }

            if (closest is not null)
            {
                return Clone(closest);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var created = new SpeakerVoiceProfile(
                Guid.CreateVersion7().ToString("n"),
                Name: null,
                normalized,
                now,
                now);

            profiles.Add(created);
            await SaveCoreAsync(profiles, cancellationToken).ConfigureAwait(false);
            return Clone(created);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeakerVoiceProfile> RenameAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string normalizedId = profileId.Trim();
        string normalizedName = name.Trim();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<SpeakerVoiceProfile> profiles = LoadCore();
            int index = profiles.FindIndex(
                profile => string.Equals(profile.Id, normalizedId, StringComparison.Ordinal));

            if (index < 0)
            {
                throw new KeyNotFoundException($"Speaker profile '{normalizedId}' does not exist.");
            }

            SpeakerVoiceProfile renamed = profiles[index] with
            {
                Name = normalizedName,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };

            profiles[index] = renamed;
            await SaveCoreAsync(profiles, cancellationToken).ConfigureAwait(false);
            return Clone(renamed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private List<SpeakerVoiceProfile> LoadCore()
    {
        try
        {
            if (!File.Exists(ProfilesPath))
            {
                return [];
            }

            string json = File.ReadAllText(ProfilesPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            List<SpeakerVoiceProfile?>? stored =
                JsonSerializer.Deserialize<List<SpeakerVoiceProfile?>>(
                    json,
                    FileSystemNoteRepository.JsonOptions);

            if (stored is null)
            {
                return [];
            }

            List<SpeakerVoiceProfile> normalized = [];
            HashSet<string> ids = new(StringComparer.Ordinal);

            foreach (SpeakerVoiceProfile? profile in stored)
            {
                if (profile is null ||
                    string.IsNullOrWhiteSpace(profile.Id) ||
                    !ids.Add(profile.Id.Trim()))
                {
                    continue;
                }

                float[] voicePrint = Normalize(profile.VoicePrint);
                if (voicePrint.Length == 0)
                {
                    continue;
                }

                DateTimeOffset created = profile.CreatedUtc == default
                    ? DateTimeOffset.UtcNow
                    : profile.CreatedUtc;
                DateTimeOffset updated = profile.UpdatedUtc == default
                    ? created
                    : profile.UpdatedUtc;

                normalized.Add(profile with
                {
                    Id = profile.Id.Trim(),
                    Name = string.IsNullOrWhiteSpace(profile.Name) ? null : profile.Name.Trim(),
                    VoicePrint = voicePrint,
                    CreatedUtc = created,
                    UpdatedUtc = updated,
                });
            }

            return normalized;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<SpeakerVoiceProfile> profiles,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(ProfilesPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(profiles, FileSystemNoteRepository.IndentedJsonOptions);
        string temp = ProfilesPath + ".tmp";

        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            File.Move(temp, ProfilesPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }

            throw;
        }
    }

    private static float[] Normalize(ReadOnlySpan<float> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        double sum = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]))
            {
                return [];
            }

            sum += (double)values[i] * values[i];
        }

        if (sum <= 0 || !double.IsFinite(sum))
        {
            return [];
        }

        var scale = (float)(1 / Math.Sqrt(sum));
        float[] normalized = new float[values.Length];
        for (var i = 0; i < normalized.Length; i++)
        {
            normalized[i] = values[i] * scale;
        }

        return normalized;
    }

    private static double CosineDistance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double similarity = 0;
        for (var i = 0; i < left.Length; i++)
        {
            similarity += (double)left[i] * right[i];
        }

        return 1 - Math.Clamp(similarity, -1, 1);
    }

    private static SpeakerVoiceProfile Clone(SpeakerVoiceProfile profile) =>
        profile with { VoicePrint = [.. profile.VoicePrint] };
}

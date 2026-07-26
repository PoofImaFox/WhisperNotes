using System.Text;
using System.Text.Json;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.Core.Configuration;

/// <summary>
/// Persists <see cref="AppSettings"/> as JSON. A missing or damaged file is never fatal — the
/// app must start on a fresh machine, and a bad settings file should cost you your preferences,
/// not your ability to take notes.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public JsonSettingsStore(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? AppSettings.DefaultSettingsPath
            : Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return Normalise(JsonSerializer.Deserialize<AppSettings>(json, FileSystemNoteRepository.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, FileSystemNoteRepository.IndentedJsonOptions);

            // Temp file + move so an interrupted save can never leave a half-written settings file.
            var temp = SettingsPath + ".tmp";
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            try
            {
                File.Move(temp, SettingsPath, overwrite: true);
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
        finally
        {
            _saveGate.Release();
        }
    }

    private static AppSettings Normalise(AppSettings? settings)
    {
        if (settings is null)
        {
            return new AppSettings();
        }

        settings.NotesRoot = Blank(settings.NotesRoot) ? AppSettings.DefaultNotesRoot : settings.NotesRoot;
        settings.ModelsRoot = Blank(settings.ModelsRoot) ? AppSettings.DefaultModelsRoot : settings.ModelsRoot;
        settings.Language = Blank(settings.Language) ? "auto" : settings.Language;
        settings.Chunking ??= new ChunkingSettings();

        // InputSources supersedes LastChannelId. Preserve the old selection as one enabled input
        // when loading a pre-multi-input settings file, and keep LastChannelId mirrored so an
        // older CLI/app build still opens on a useful endpoint.
        settings.InputSources ??= [];
        if (settings.InputSources.Count == 0 && !Blank(settings.LastChannelId))
        {
            settings.InputSources.Add(new InputSourceSettings
            {
                Id = "primary",
                DisplayName = "Primary input",
                ChannelId = settings.LastChannelId!.Trim(),
                Enabled = true,
            });
        }

        NormaliseInputSources(settings);
        NormaliseAi(settings);
        NormaliseDiarization(settings);

        return settings;
    }

    private static void NormaliseAi(AppSettings settings)
    {
        // A settings file written before the AI layer existed has no "ai" node at all.
        settings.Ai ??= new AiSettings();
        settings.Ai.OllamaEndpoint = Blank(settings.Ai.OllamaEndpoint)
            ? new AiSettings().OllamaEndpoint
            : settings.Ai.OllamaEndpoint.Trim();
        settings.Ai.OllamaModel = Blank(settings.Ai.OllamaModel)
            ? new AiSettings().OllamaModel
            : settings.Ai.OllamaModel.Trim();
        settings.Ai.AnthropicModel = Blank(settings.Ai.AnthropicModel)
            ? new AiSettings().AnthropicModel
            : settings.Ai.AnthropicModel.Trim();
        settings.Ai.MaxOutputTokens = settings.Ai.MaxOutputTokens > 0 ? settings.Ai.MaxOutputTokens : 8000;
        settings.Ai.TimeoutSeconds = settings.Ai.TimeoutSeconds > 0 ? settings.Ai.TimeoutSeconds : 300;
    }

    private static void NormaliseDiarization(AppSettings settings)
    {
        // A settings file written before diarization existed has no "diarization" node at all.
        settings.Diarization ??= new DiarizationSettings();
        settings.Diarization.MaxSpeakers = settings.Diarization.MaxSpeakers > 0 ? settings.Diarization.MaxSpeakers : 8;

        // Cosine distance between two L2-normalised vectors cannot exceed 2; a value outside that
        // would either merge every voice into one or split every utterance into its own speaker.
        settings.Diarization.MergeThreshold = settings.Diarization.MergeThreshold is > 0 and <= 2
            ? settings.Diarization.MergeThreshold
            : 0.65;
        settings.Diarization.MinObservationSeconds = settings.Diarization.MinObservationSeconds > 0
            ? settings.Diarization.MinObservationSeconds
            : 0.35;
        settings.Diarization.MaxObservationSeconds = settings.Diarization.MaxObservationSeconds > 0
            ? settings.Diarization.MaxObservationSeconds
            : 8;

        // A window whose ceiling sits under its floor embeds nothing at all, so the ceiling gives way.
        settings.Diarization.MaxObservationSeconds = Math.Max(
            settings.Diarization.MaxObservationSeconds,
            settings.Diarization.MinObservationSeconds);
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static void NormaliseInputSources(AppSettings settings)
    {
        List<InputSourceSettings> normalised = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);

        foreach (InputSourceSettings? input in settings.InputSources)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.ChannelId))
            {
                continue;
            }

            string baseId = string.IsNullOrWhiteSpace(input.Id)
                ? $"input-{normalised.Count + 1}"
                : input.Id.Trim();
            string id = baseId;
            for (var suffix = 2; !ids.Add(id); suffix++)
            {
                id = $"{baseId}-{suffix}";
            }

            input.Id = id;
            input.ChannelId = input.ChannelId.Trim();
            input.Kind = Enum.IsDefined(input.Kind) ? input.Kind : Audio.AudioChannelKind.Loopback;
            input.DisplayName = string.IsNullOrWhiteSpace(input.DisplayName)
                ? input.Kind == Audio.AudioChannelKind.Microphone ? "Microphone" : "System audio"
                : input.DisplayName.Trim();
            normalised.Add(input);
        }

        settings.InputSources = normalised;

        InputSourceSettings? primary = normalised.FirstOrDefault(input => input.Enabled)
                                            ?? normalised.FirstOrDefault();
        if (primary is not null)
        {
            settings.LastChannelId = primary.ChannelId;
        }
    }
}

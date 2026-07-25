using System.Text;
using System.Text.Json;
using NoteScribe.Core.Notes;

namespace NoteScribe.Core.Configuration;

/// <summary>
/// Persists <see cref="AppSettings"/> as JSON. A missing or damaged file is never fatal — the
/// app must start on a fresh machine, and a bad settings file should cost you your preferences,
/// not your ability to take notes.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
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
        return settings;

        static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    }
}

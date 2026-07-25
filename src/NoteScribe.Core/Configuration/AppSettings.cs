using NoteScribe.Core.Transcription;

namespace NoteScribe.Core.Configuration;

/// <summary>
/// User-tunable settings, persisted as JSON next to the app data so the CLI and UI agree.
/// Every property has a working default — a first run must not require configuration.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Root of the organised notes tree. Defaults to %USERPROFILE%\Documents\NoteScribe.</summary>
    public string NotesRoot { get; set; } = DefaultNotesRoot;

    /// <summary>Where ggml weights are cached. Defaults to %LOCALAPPDATA%\NoteScribe\models.</summary>
    public string ModelsRoot { get; set; } = DefaultModelsRoot;

    /// <summary>Which weights to run.</summary>
    public WhisperModelSize Model { get; set; } = WhisperModelSize.Base;

    /// <summary>ISO language code, or "auto".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Decoder threads. Null means Environment.ProcessorCount capped at 8.</summary>
    public int? Threads { get; set; }

    /// <summary>Persisted channel selection so the app reopens on the right endpoint.</summary>
    public string? LastChannelId { get; set; }

    /// <summary>Default project name applied to new sessions.</summary>
    public string? DefaultProject { get; set; }

    /// <summary>Vocabulary hint fed to whisper — client names, product names, acronyms.</summary>
    public string? InitialPrompt { get; set; }

    /// <summary>Keep the captured WAV alongside the notes. Off by default to save disk.</summary>
    public bool KeepSessionAudio { get; set; }

    /// <summary>Explicit ffmpeg path. Null means "resolve from PATH".</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Live chunking tuning.</summary>
    public ChunkingSettings Chunking { get; set; } = new();

    public static string DefaultNotesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NoteScribe");

    public static string DefaultModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteScribe", "models");

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoteScribe", "settings.json");

    public TranscriptionOptions ToTranscriptionOptions() =>
        new(Model, Language, Threads, Translate: false, InitialPrompt);

    public ChunkingOptions ToChunkingOptions() => new(
        TimeSpan.FromSeconds(Chunking.MinChunkSeconds),
        TimeSpan.FromSeconds(Chunking.MaxChunkSeconds),
        TimeSpan.FromMilliseconds(Chunking.SilenceMilliseconds),
        Chunking.SilenceThreshold);
}

/// <summary>Plain-number mirror of <see cref="ChunkingOptions"/> so the JSON stays readable.</summary>
public sealed class ChunkingSettings
{
    public double MinChunkSeconds { get; set; } = 2;
    public double MaxChunkSeconds { get; set; } = 15;
    public double SilenceMilliseconds { get; set; } = 700;
    public float SilenceThreshold { get; set; } = 0.006f;
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    AppSettings Load();
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
    string SettingsPath { get; }
}

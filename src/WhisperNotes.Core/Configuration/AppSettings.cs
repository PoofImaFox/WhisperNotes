using WhisperNotes.Core.Ai;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Configuration;

/// <summary>
/// User-tunable settings, persisted as JSON next to the app data so the CLI and UI agree.
/// Every property has a working default — a first run must not require configuration.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Root of the organised notes tree. Defaults to %USERPROFILE%\Documents\WhisperNotes.</summary>
    public string NotesRoot { get; set; } = DefaultNotesRoot;

    /// <summary>Where ggml weights are cached. Defaults to %LOCALAPPDATA%\WhisperNotes\models.</summary>
    public string ModelsRoot { get; set; } = DefaultModelsRoot;

    /// <summary>Which weights to run.</summary>
    public WhisperModelSize Model { get; set; } = WhisperModelSize.Base;

    /// <summary>ISO language code, or "auto".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Decoder threads. Null means Environment.ProcessorCount capped at 8.</summary>
    /// <remarks>Barely matters once <see cref="Gpu"/> is on — the decode is not on the CPU then.</remarks>
    public int? Threads { get; set; }

    /// <summary>GPU decode. On by default; see <see cref="GpuSettings"/>.</summary>
    public GpuSettings Gpu { get; set; } = new();

    /// <summary>Persisted channel selection so the app reopens on the right endpoint.</summary>
    /// <remarks>
    /// Kept for compatibility with older settings files and CLI versions. New desktop capture
    /// uses <see cref="InputSources"/> and mirrors its primary enabled source here.
    /// </remarks>
    public string? LastChannelId { get; set; }

    /// <summary>
    /// Audio endpoints captured together. An empty collection means no sources have been
    /// configured yet; the desktop input page will offer the current default endpoint.
    /// </summary>
    public List<InputSourceSettings> InputSources { get; set; } = [];

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

    /// <summary>Speaker attribution tuning.</summary>
    public DiarizationSettings Diarization { get; set; } = new();

    /// <summary>Assistant provider and model. Defaults to local Ollama so nothing leaves the machine.</summary>
    public AiSettings Ai { get; set; } = new();

    public static string DefaultNotesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhisperNotes");

    public static string DefaultModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperNotes", "models");

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperNotes", "settings.json");

    public TranscriptionOptions ToTranscriptionOptions() =>
        new(Model, Language, Threads, Translate: false, InitialPrompt, Gpu.Enabled, Gpu.Device);

    public ChunkingOptions ToChunkingOptions() => new(
        TimeSpan.FromSeconds(Chunking.MinChunkSeconds),
        TimeSpan.FromSeconds(Chunking.MaxChunkSeconds),
        TimeSpan.FromMilliseconds(Chunking.SilenceMilliseconds),
        Chunking.SilenceThreshold);

    public DiarizationOptions ToDiarizationOptions() => new()
    {
        Enabled = Diarization.Enabled,
        MaxSpeakers = Diarization.MaxSpeakers,
        MergeThreshold = Diarization.MergeThreshold,
        MinObservation = TimeSpan.FromSeconds(Diarization.MinObservationSeconds),
        MaxObservation = TimeSpan.FromSeconds(Diarization.MaxObservationSeconds)
    };
}

/// <summary>A durable reference to one independently captured audio endpoint.</summary>
public sealed class InputSourceSettings
{
    /// <summary>
    /// Stable identity for this configuration row. This is deliberately separate from
    /// <see cref="ChannelId"/> so the user can change devices without changing the input's identity.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>User-facing label used to identify this source in a combined transcript.</summary>
    public string DisplayName { get; set; } = "Input";

    /// <summary>Stable WASAPI endpoint identifier resolved by <c>IAudioChannelEnumerator</c>.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The endpoint role last observed when the setting was saved.</summary>
    public AudioChannelKind Kind { get; set; } = AudioChannelKind.Loopback;

    /// <summary>Disabled sources remain configured but are not captured.</summary>
    public bool Enabled { get; set; } = true;

    public InputSourceSettings Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ChannelId = ChannelId,
        Kind = Kind,
        Enabled = Enabled,
    };
}

/// <summary>
/// Which model answers the assistant's quick actions, and how patient we are with it.
/// </summary>
/// <remarks>
/// Ollama is the default so the offline promise in the README holds unless the user deliberately
/// opts in to a hosted provider. There are no temperature/top-p/top-k knobs here on purpose: the
/// Anthropic model family this targets rejects them outright.
/// </remarks>
public sealed class AiSettings
{
    public AiProviderKind Provider { get; set; } = AiProviderKind.Ollama;

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "llama3.1";

    public string AnthropicModel { get; set; } = "claude-opus-5";

    /// <summary>Falls back to the ANTHROPIC_API_KEY environment variable when null/blank.</summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>
    /// Hard cap on the answer. On thinking-by-default models this covers reasoning as well as the
    /// visible text, so it is deliberately generous.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 8000;

    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>Where the decode runs.</summary>
/// <remarks>
/// There is no "which backend" setting here on purpose. Whisper.net picks the native runtime once
/// per process from what the machine actually supports, and a stale preference in a settings file
/// that outlives a driver or GPU change would only ever be wrong. Run <c>whispernotes doctor</c> to
/// see what it chose.
/// </remarks>
public sealed class GpuSettings
{
    /// <summary>
    /// Roughly a 40x difference on large-v3-turbo, so this is only worth clearing to work around a
    /// driver that crashes or produces garbage.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Index into the adapter list <c>whispernotes doctor</c> prints. Zero is the driver's first,
    /// which on a desktop with an active integrated GPU is not reliably the discrete card.
    /// </summary>
    public int Device { get; set; }
}

/// <summary>Plain-number mirror of <see cref="ChunkingOptions"/> so the JSON stays readable.</summary>
public sealed class ChunkingSettings
{
    public double MinChunkSeconds { get; set; } = 2;
    public double MaxChunkSeconds { get; set; } = 15;
    public double SilenceMilliseconds { get; set; } = 700;
    public float SilenceThreshold { get; set; } = 0.006f;
}

/// <summary>Plain-number mirror of <see cref="DiarizationOptions"/> so the JSON stays readable.</summary>
public sealed class DiarizationSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxSpeakers { get; set; } = 8;

    /// <summary>Cosine distance, so 0..2 rather than 0..1 — see <see cref="DiarizationOptions.MergeThreshold"/>.</summary>
    public double MergeThreshold { get; set; } = 0.6;
    public double MinObservationSeconds { get; set; } = 1.2;
    public double MaxObservationSeconds { get; set; } = 8;
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    AppSettings Load();
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
    string SettingsPath { get; }
}

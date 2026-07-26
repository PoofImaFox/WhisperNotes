using System.Collections.Frozen;
using System.CommandLine;
using System.Globalization;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Configuration;

namespace WhisperNotes.Cli.Commands;

/// <summary>Reads and writes the persisted <see cref="AppSettings"/>.</summary>
internal static class ConfigCommand
{
    /// <summary>Two clear of the longest key, so no row runs its name into its value.</summary>
    private const int KeyWidth = 35;

    /// <summary>
    /// Every setting the CLI exposes, in the order <c>config show</c> prints them.
    /// </summary>
    /// <remarks>
    /// Reader and writer sit on the same row deliberately. They used to be two independent lists —
    /// a <c>switch</c> that set keys and a method that printed them — so a key added to one and not
    /// the other would have been shown but not settable, or the reverse, with nothing to catch it.
    /// </remarks>
    private static readonly SettingDefinition[] Definitions =
    [
        new("NotesRoot",
            static s => s.NotesRoot,
            static (s, v) => s.NotesRoot = FullPath(v, AppSettings.DefaultNotesRoot)),
        new("ModelsRoot",
            static s => s.ModelsRoot,
            static (s, v) => s.ModelsRoot = FullPath(v, AppSettings.DefaultModelsRoot)),
        new("Model",
            static s => ModelSizes.Name(s.Model),
            static (s, v) => s.Model = ModelSizes.Parse(v.Trimmed)),
        new("Language",
            static s => s.Language,
            static (s, v) => s.Language = TextOr(v, "auto")),
        new("Threads",
            static s => Optional(s.Threads, "(auto)"),
            static (s, v) => s.Threads = PositiveIntOrNull(v)),
        new("Gpu.Enabled",
            static s => Flag(s.Gpu.Enabled),
            static (s, v) => s.Gpu.Enabled = Boolean(v.Key, v.Trimmed)),
        new("Gpu.Device",
            static s => Whole(s.Gpu.Device),
            static (s, v) => s.Gpu.Device = DeviceIndex(v.Key, v.Trimmed)),
        new("LastChannelId",
            static s => Optional(s.LastChannelId, "(none)"),
            static (s, v) => s.LastChannelId = TextOrNull(v)),
        new("DefaultProject",
            static s => Optional(s.DefaultProject, "(none)"),
            static (s, v) => s.DefaultProject = TextOrNull(v)),
        new("InitialPrompt",
            static s => Optional(s.InitialPrompt, "(none)"),
            static (s, v) => s.InitialPrompt = VerbatimOrNull(v)),
        new("KeepSessionAudio",
            static s => Flag(s.KeepSessionAudio),
            static (s, v) => s.KeepSessionAudio = Boolean(v.Key, v.Trimmed)),
        new("FfmpegPath",
            static s => Optional(s.FfmpegPath, "(resolved from PATH)"),
            static (s, v) => s.FfmpegPath = TextOrNull(v)),
        new("Chunking.MinChunkSeconds",
            static s => Number(s.Chunking.MinChunkSeconds),
            static (s, v) => s.Chunking.MinChunkSeconds = PositiveDouble(v.Key, v.Trimmed)),
        new("Chunking.MaxChunkSeconds",
            static s => Number(s.Chunking.MaxChunkSeconds),
            static (s, v) => s.Chunking.MaxChunkSeconds = PositiveDouble(v.Key, v.Trimmed)),
        new("Chunking.SilenceMilliseconds",
            static s => Number(s.Chunking.SilenceMilliseconds),
            static (s, v) => s.Chunking.SilenceMilliseconds = PositiveDouble(v.Key, v.Trimmed)),
        new("Chunking.SilenceThreshold",
            static s => Number(s.Chunking.SilenceThreshold),
            static (s, v) => s.Chunking.SilenceThreshold = (float)Fraction(v.Key, v.Trimmed)),
        new("Diarization.Enabled",
            static s => Flag(s.Diarization.Enabled),
            static (s, v) => s.Diarization.Enabled = Boolean(v.Key, v.Trimmed)),
        new("Diarization.MaxSpeakers",
            static s => Whole(s.Diarization.MaxSpeakers),
            static (s, v) => s.Diarization.MaxSpeakers = PositiveInt(v.Key, v.Trimmed)),
        new("Diarization.MergeThreshold",
            static s => Number(s.Diarization.MergeThreshold),
            static (s, v) => s.Diarization.MergeThreshold = CosineDistance(v.Key, v.Trimmed)),
        new("Diarization.MinObservationSeconds",
            static s => Number(s.Diarization.MinObservationSeconds),
            static (s, v) => s.Diarization.MinObservationSeconds = PositiveDouble(v.Key, v.Trimmed)),
        new("Diarization.MaxObservationSeconds",
            static s => Number(s.Diarization.MaxObservationSeconds),
            static (s, v) => s.Diarization.MaxObservationSeconds = PositiveDouble(v.Key, v.Trimmed))
    ];

    /// <summary>Both accepted spellings of every key, folded into one lookup.</summary>
    private static readonly FrozenDictionary<string, SettingDefinition> ByKey = BuildIndex();

    private static readonly Argument<string> KeyArgument = new("key")
    {
        Description = "Setting name, e.g. NotesRoot."
    };

    private static readonly Argument<string> ValueArgument = new("value")
    {
        Description = "New value. Use an empty string to clear an optional setting."
    };

    public static Command Create()
    {
        Command command = new("config", "Show and change the persisted settings.");
        command.Subcommands.Add(ShowCommand());
        command.Subcommands.Add(SetCommand());
        command.Subcommands.Add(PathCommand());
        return command;
    }

    private static Command ShowCommand()
    {
        Command command = new("show", "Print effective settings and the settings file path.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, environment =>
            {
                ConsoleOutput console = environment.Console;
                AppSettings settings = environment.Settings;

                console.Result();
                console.Result($"  settings  {environment.SettingsPath}"
                               + (File.Exists(environment.SettingsPath) ? string.Empty : "  (not created yet)"));
                console.Result();

                foreach (SettingDefinition definition in Definitions)
                {
                    console.Result("  " + definition.Key.PadRight(KeyWidth) + definition.Read(settings));
                }

                console.Result();
                return Task.FromResult(ExitCode.Success);
            }));

        return command;
    }

    private static Command SetCommand()
    {
        Command command = new("set", "Set one value, e.g. config set NotesRoot \"D:\\Notes\".");
        command.Arguments.Add(KeyArgument);
        command.Arguments.Add(ValueArgument);

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, async environment =>
            {
                var key = parseResult.GetRequiredValue(KeyArgument);
                var value = parseResult.GetRequiredValue(ValueArgument);

                // Deliberately reloaded from disk: writing the merged view would bake this
                // invocation's --notes-root/--models-root overrides into the file.
                JsonSettingsStore store = new();
                AppSettings settings = store.Load();

                SettingDefinition definition = Apply(settings, key, value);
                await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);

                environment.Console.Result($"{definition.Key} = {definition.Read(settings)}");
                environment.Console.Line($"  saved to {store.SettingsPath}");

                return ExitCode.Success;
            }));

        return command;
    }

    private static Command PathCommand()
    {
        Command command = new("path", "Print the settings file path.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, environment =>
            {
                environment.Console.Result(environment.SettingsPath);
                return Task.FromResult(ExitCode.Success);
            }));

        return command;
    }

    /// <summary>Parses one value onto <paramref name="settings"/> and reports the key it landed on.</summary>
    private static SettingDefinition Apply(AppSettings settings, string key, string value)
    {
        SettingDefinition definition = Resolve(key);
        definition.Write(settings, new SettingValue(key, value));
        ValidateInvariants(settings);
        return definition;
    }

    private static SettingDefinition Resolve(string key) =>
        ByKey.TryGetValue(key, out SettingDefinition? definition)
            ? definition
            : throw new CliException(
                ExitCode.Usage,
                $"Unknown setting '{key}'. Valid keys:" + Environment.NewLine
                + string.Join(Environment.NewLine, Definitions.Select(static d => "  " + d.Key)));

    /// <summary>The cross-field rules that a single-key edit can still leave inverted.</summary>
    private static void ValidateInvariants(AppSettings settings)
    {
        if (settings.Chunking.MaxChunkSeconds < settings.Chunking.MinChunkSeconds)
        {
            throw new CliException(
                ExitCode.Usage,
                "Chunking.MaxChunkSeconds must be at least Chunking.MinChunkSeconds.");
        }

        if (settings.Diarization.MaxObservationSeconds < settings.Diarization.MinObservationSeconds)
        {
            throw new CliException(
                ExitCode.Usage,
                "Diarization.MaxObservationSeconds must be at least Diarization.MinObservationSeconds.");
        }
    }

    /// <summary>
    /// Keys match case-insensitively, and their dots are optional: <c>chunkingminchunkseconds</c>
    /// has always been as good as <c>Chunking.MinChunkSeconds</c>.
    /// </summary>
    private static FrozenDictionary<string, SettingDefinition> BuildIndex()
    {
        Dictionary<string, SettingDefinition> index = new(StringComparer.OrdinalIgnoreCase);

        foreach (SettingDefinition definition in Definitions)
        {
            // TryAdd, not the indexer: the first definition to claim a spelling keeps it, which is
            // the order the old first-match-wins scan resolved in.
            index.TryAdd(definition.Key, definition);
            index.TryAdd(definition.Key.Replace(".", string.Empty, StringComparison.Ordinal), definition);
        }

        return index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "true" : "false";

    private static string Optional(string? value, string fallback) => value ?? fallback;

    private static string Optional(int? value, string fallback) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? fallback;

    private static string FullPath(SettingValue value, string fallback) =>
        value.Blank ? fallback : Path.GetFullPath(value.Trimmed);

    private static string TextOr(SettingValue value, string fallback) =>
        value.Blank ? fallback : value.Trimmed;

    private static string? TextOrNull(SettingValue value) => value.Blank ? null : value.Trimmed;

    /// <summary>Untrimmed on purpose: a vocabulary hint is fed to whisper exactly as typed.</summary>
    private static string? VerbatimOrNull(SettingValue value) => value.Blank ? null : value.Raw;

    private static int? PositiveIntOrNull(SettingValue value) =>
        value.Blank ? null : PositiveInt(value.Key, value.Trimmed);

    private static int PositiveInt(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new CliException(ExitCode.Usage, $"{key} needs a positive whole number, got '{value}'.");

    /// <summary>An index into the adapter list, so unlike every other count here zero is valid.</summary>
    private static int DeviceIndex(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new CliException(
                ExitCode.Usage,
                $"{key} is an adapter index as listed by 'whispernotes doctor', so it needs 0 or more, got '{value}'.");

    private static double PositiveDouble(string key, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new CliException(ExitCode.Usage, $"{key} needs a positive number, got '{value}'.");

    private static double Fraction(string key, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 1
            ? parsed
            : throw new CliException(ExitCode.Usage, $"{key} needs a number between 0 and 1, got '{value}'.");

    /// <summary>Wider than <see cref="Fraction"/>: two opposed unit vectors are 2 apart, not 1.</summary>
    private static double CosineDistance(string key, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 2
            ? parsed
            : throw new CliException(ExitCode.Usage, $"{key} needs a cosine distance above 0 and no more than 2, got '{value}'.");

    private static bool Boolean(string key, string value) =>
        value.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new CliException(ExitCode.Usage, $"{key} needs true or false, got '{value}'.")
        };

    /// <summary>One configurable key: how it is printed, and how the same key is parsed back in.</summary>
    private sealed record SettingDefinition(
        string Key,
        Func<AppSettings, string> Read,
        Action<AppSettings, SettingValue> Write);

    /// <summary>
    /// The arguments of one <c>config set</c>. The key is carried untouched because every parse
    /// failure quotes back the spelling the user actually typed, not the canonical one.
    /// </summary>
    private readonly record struct SettingValue(string Key, string Raw)
    {
        /// <summary>What every setting but <c>InitialPrompt</c> parses.</summary>
        public string Trimmed { get; } = Raw.Trim();

        public bool Blank => Trimmed.Length == 0;
    }
}

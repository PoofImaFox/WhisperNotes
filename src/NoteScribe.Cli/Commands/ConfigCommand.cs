using System.CommandLine;
using System.Globalization;
using NoteScribe.Cli.Rendering;
using NoteScribe.Core.Configuration;

namespace NoteScribe.Cli.Commands;

/// <summary>Reads and writes the persisted <see cref="AppSettings"/>.</summary>
internal static class ConfigCommand
{
    private const int KeyWidth = 30;

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

                foreach ((string key, string value) in Describe(settings))
                {
                    console.Result("  " + key.PadRight(KeyWidth) + value);
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

                Apply(settings, key, value);
                await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);

                environment.Console.Result($"{Canonical(key)} = {Describe(settings).First(p => Matches(p.Key, key)).Value}");
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

    private static IEnumerable<(string Key, string Value)> Describe(AppSettings settings)
    {
        yield return ("NotesRoot", settings.NotesRoot);
        yield return ("ModelsRoot", settings.ModelsRoot);
        yield return ("Model", ModelSizes.Name(settings.Model));
        yield return ("Language", settings.Language);
        yield return ("Threads", settings.Threads?.ToString(CultureInfo.InvariantCulture) ?? "(auto)");
        yield return ("LastChannelId", settings.LastChannelId ?? "(none)");
        yield return ("DefaultProject", settings.DefaultProject ?? "(none)");
        yield return ("InitialPrompt", settings.InitialPrompt ?? "(none)");
        yield return ("KeepSessionAudio", settings.KeepSessionAudio ? "true" : "false");
        yield return ("FfmpegPath", settings.FfmpegPath ?? "(resolved from PATH)");
        yield return ("Chunking.MinChunkSeconds", Number(settings.Chunking.MinChunkSeconds));
        yield return ("Chunking.MaxChunkSeconds", Number(settings.Chunking.MaxChunkSeconds));
        yield return ("Chunking.SilenceMilliseconds", Number(settings.Chunking.SilenceMilliseconds));
        yield return ("Chunking.SilenceThreshold", Number(settings.Chunking.SilenceThreshold));
    }

    private static void Apply(AppSettings settings, string key, string value)
    {
        var trimmed = value.Trim();
        var blank = trimmed.Length == 0;

        switch (Canonical(key))
        {
            case "NotesRoot":
                settings.NotesRoot = blank ? AppSettings.DefaultNotesRoot : Path.GetFullPath(trimmed);
                break;
            case "ModelsRoot":
                settings.ModelsRoot = blank ? AppSettings.DefaultModelsRoot : Path.GetFullPath(trimmed);
                break;
            case "Model":
                settings.Model = ModelSizes.Parse(trimmed);
                break;
            case "Language":
                settings.Language = blank ? "auto" : trimmed;
                break;
            case "Threads":
                settings.Threads = blank ? null : PositiveInt(key, trimmed);
                break;
            case "LastChannelId":
                settings.LastChannelId = blank ? null : trimmed;
                break;
            case "DefaultProject":
                settings.DefaultProject = blank ? null : trimmed;
                break;
            case "InitialPrompt":
                settings.InitialPrompt = blank ? null : value;
                break;
            case "KeepSessionAudio":
                settings.KeepSessionAudio = Boolean(key, trimmed);
                break;
            case "FfmpegPath":
                settings.FfmpegPath = blank ? null : trimmed;
                break;
            case "Chunking.MinChunkSeconds":
                settings.Chunking.MinChunkSeconds = PositiveDouble(key, trimmed);
                break;
            case "Chunking.MaxChunkSeconds":
                settings.Chunking.MaxChunkSeconds = PositiveDouble(key, trimmed);
                break;
            case "Chunking.SilenceMilliseconds":
                settings.Chunking.SilenceMilliseconds = PositiveDouble(key, trimmed);
                break;
            case "Chunking.SilenceThreshold":
                settings.Chunking.SilenceThreshold = (float)Fraction(key, trimmed);
                break;
            default:
                throw new CliException(
                    ExitCode.Usage,
                    $"Unknown setting '{key}'. Valid keys:" + Environment.NewLine
                    + string.Join(Environment.NewLine, Describe(new AppSettings()).Select(static p => "  " + p.Key)));
        }

        if (settings.Chunking.MaxChunkSeconds < settings.Chunking.MinChunkSeconds)
        {
            throw new CliException(
                ExitCode.Usage,
                "Chunking.MaxChunkSeconds must be at least Chunking.MinChunkSeconds.");
        }
    }

    private static string Canonical(string key)
    {
        foreach ((string known, _) in Describe(new AppSettings()))
        {
            if (Matches(known, key))
            {
                return known;
            }
        }

        return key;
    }

    private static bool Matches(string known, string requested) =>
        string.Equals(known, requested, StringComparison.OrdinalIgnoreCase)
        || string.Equals(known.Replace(".", string.Empty, StringComparison.Ordinal), requested, StringComparison.OrdinalIgnoreCase);

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static int PositiveInt(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new CliException(ExitCode.Usage, $"{key} needs a positive whole number, got '{value}'.");

    private static double PositiveDouble(string key, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new CliException(ExitCode.Usage, $"{key} needs a positive number, got '{value}'.");

    private static double Fraction(string key, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 1
            ? parsed
            : throw new CliException(ExitCode.Usage, $"{key} needs a number between 0 and 1, got '{value}'.");

    private static bool Boolean(string key, string value) =>
        value.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new CliException(ExitCode.Usage, $"{key} needs true or false, got '{value}'.")
        };
}

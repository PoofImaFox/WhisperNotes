using System.CommandLine;
using NoteScribe.Cli.Rendering;
using NoteScribe.Core.Composition;
using NoteScribe.Core.Transcription;

namespace NoteScribe.Cli.Commands;

/// <summary>
/// Inspects and pre-fetches whisper weights. Pre-downloading matters: the first <c>listen</c> on an
/// un-fetched <c>medium</c> would otherwise stall for a 1.5 GB download as the meeting starts.
/// </summary>
internal static class ModelsCommand
{
    private static readonly Argument<string> SizeArgument = new Argument<string>("size")
    {
        Description = "Which weights to fetch."
    }.AcceptOnlyFromAmong(ModelSizes.Names);

    public static Command Create()
    {
        Command command = new("models", "Inspect and pre-fetch the whisper weights.");
        command.Subcommands.Add(ListCommand());
        command.Subcommands.Add(DownloadCommand());
        command.Subcommands.Add(PathCommand());
        return command;
    }

    private static Command ListCommand()
    {
        Command command = new("list", "Show each model, its size on disk, and whether it's downloaded.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, async environment =>
            {
                await using NoteScribeServices services = NoteScribeServices.Create(environment.Settings);
                ConsoleOutput console = environment.Console;

                console.Result();
                console.Result("  MODEL           STATUS      SIZE");

                foreach (WhisperModelSize size in ModelSizes.All)
                {
                    var downloaded = services.Models.IsDownloaded(size);
                    var path = services.Models.GetModelPath(size);
                    var bytes = downloaded ? SafeLength(path) : -1;

                    console.Result(
                        "  " + ModelSizes.Name(size).PadRight(16)
                        + (downloaded ? "downloaded" : "missing").PadRight(12)
                        + (bytes >= 0 ? Format.Bytes(bytes) : "-"));

                    console.Diagnostic(path);
                }

                console.Result();
                console.Result($"  models  {services.Settings.ModelsRoot}");
                console.Result();

                return ExitCode.Success;
            }));

        return command;
    }

    private static Command DownloadCommand()
    {
        Command command = new("download", "Pre-fetch weights with a progress bar.");
        command.Arguments.Add(SizeArgument);

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, async environment =>
            {
                ConsoleOutput console = environment.Console;
                WhisperModelSize size = ModelSizes.Parse(parseResult.GetRequiredValue(SizeArgument));

                ModelDownloadReporter reporter = new(console);
                await using NoteScribeServices services = NoteScribeServices.Create(environment.Settings, reporter);

                console.Line();

                if (services.Models.IsDownloaded(size))
                {
                    console.Field("model", $"{ModelSizes.Name(size)} (already downloaded)", 9);
                    console.Result(services.Models.GetModelPath(size));
                    return ExitCode.Success;
                }

                console.Field("model", $"{ModelSizes.Name(size)} (fetching)", 9);

                string path;
                try
                {
                    path = await services.Models
                        .EnsureDownloadedAsync(size, reporter, cancellationToken)
                        .ConfigureAwait(false);
                    reporter.Finish();
                }
                catch (OperationCanceledException)
                {
                    reporter.Abandon();
                    throw;
                }
                catch (Exception ex)
                {
                    reporter.Abandon();
                    throw new CliException(
                        ExitCode.ModelUnavailable,
                        $"Could not download the '{ModelSizes.Name(size)}' weights into {services.Settings.ModelsRoot}: {ex.Message}",
                        ex);
                }

                console.Result(path);
                return ExitCode.Success;
            }));

        return command;
    }

    private static Command PathCommand()
    {
        Command command = new("path", "Print the models directory.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, environment =>
            {
                environment.Console.Result(environment.Settings.ModelsRoot);
                return Task.FromResult(ExitCode.Success);
            }));

        return command;
    }

    private static long SafeLength(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}

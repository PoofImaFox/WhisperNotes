using System.CommandLine;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Composition;
using WhisperNotes.Core.Notes.Documents;
using WhisperNotes.Core.Notes.Exporting;

namespace WhisperNotes.Cli.Commands;

/// <summary>Writes authored notes to portable files, or the whole library as an Obsidian vault.</summary>
internal static class ExportCommand
{
    private const string ObsidianFormat = "obsidian";

    private static readonly Argument<string> FormatArgument = new Argument<string>("format")
    {
        Description = "markdown, html, pdf, or obsidian."
    }.AcceptOnlyFromAmong("markdown", "html", "pdf", ObsidianFormat);

    private static readonly Option<string> DocumentOption = new("--document", "-d")
    {
        Description = "Authored note id or exact title. Required except for an Obsidian library export.",
        HelpName = "id-or-title"
    };

    private static readonly Option<string> OutputOption = new("--output", "-o")
    {
        Description = "Destination file or existing directory. Defaults to the current directory.",
        HelpName = "path"
    };

    private static readonly Option<bool> OverwriteOption = new("--overwrite")
    {
        Description = "Replace an existing destination file."
    };

    public static Command Create()
    {
        Command command = new("export", "Export an authored note, or the whole library for Obsidian.");
        command.Arguments.Add(FormatArgument);
        command.Options.Add(DocumentOption);
        command.Options.Add(OutputOption);
        command.Options.Add(OverwriteOption);

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(
                parseResult,
                environment => ExecuteAsync(parseResult, environment, cancellationToken)));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        CliEnvironment environment,
        CancellationToken cancellationToken)
    {
        var requestedFormat = parseResult.GetRequiredValue(FormatArgument);
        var documentSelector = parseResult.GetValue(DocumentOption);

        await using WhisperNotesServices services = WhisperNotesServices.Create(environment.Settings);
        IReadOnlyList<NoteDocument> documents = await services.Documents
            .ListAsync(search: null, cancellationToken)
            .ConfigureAwait(false);

        INoteExportService exporter = new NoteExportService();
        NoteExportArtifact artifact;

        if (string.Equals(requestedFormat, ObsidianFormat, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(documentSelector))
            {
                throw new CliException(
                    ExitCode.Usage,
                    "--document cannot be combined with an Obsidian export; Obsidian exports the whole authored-note library.");
            }

            artifact = exporter.ExportObsidian(documents);
        }
        else
        {
            NoteDocument document = ResolveDocument(documents, documentSelector);
            artifact = exporter.Export(document, ParseFormat(requestedFormat));
        }

        var destination = ResolveDestination(parseResult.GetValue(OutputOption), artifact.SuggestedFileName);
        await WriteArtifactAsync(
                destination,
                artifact.Content,
                parseResult.GetValue(OverwriteOption),
                cancellationToken)
            .ConfigureAwait(false);

        environment.Console.Result(destination);
        return ExitCode.Success;
    }

    private static NoteDocument ResolveDocument(
        IReadOnlyList<NoteDocument> documents,
        string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new CliException(
                ExitCode.Usage,
                "--document <id-or-title> is required for Markdown, HTML, and PDF exports.");
        }

        var requested = selector.Trim();
        NoteDocument? byId = documents.FirstOrDefault(
            d => string.Equals(d.Id, requested, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId;
        }

        NoteDocument[] byTitle = documents
            .Where(d => string.Equals(d.Title, requested, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        return byTitle.Length switch
        {
            1 => byTitle[0],
            0 => throw new CliException(
                ExitCode.Usage,
                $"No authored note has id or exact title '{requested}'."),
            _ => throw new CliException(
                ExitCode.Usage,
                $"More than one authored note is titled '{requested}'. Use one of these ids:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, byTitle.Select(static d => "  " + d.Id)))
        };
    }

    private static NoteExportFormat ParseFormat(string format) =>
        format.ToLowerInvariant() switch
        {
            "markdown" => NoteExportFormat.Markdown,
            "html" => NoteExportFormat.Html,
            "pdf" => NoteExportFormat.Pdf,
            _ => throw new CliException(ExitCode.Usage, $"Unknown export format '{format}'.")
        };

    private static string ResolveDestination(string? requested, string suggestedFileName)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(suggestedFileName);
        }

        var fullPath = Path.GetFullPath(requested);
        return Directory.Exists(fullPath)
            ? Path.Combine(fullPath, suggestedFileName)
            : fullPath;
    }

    private static async Task WriteArtifactAsync(
        string destination,
        byte[] content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && !overwrite)
        {
            throw new CliException(
                ExitCode.Usage,
                $"The destination already exists: {destination}. Pass --overwrite to replace it.");
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the actual export failure is the useful error.
        }
    }
}

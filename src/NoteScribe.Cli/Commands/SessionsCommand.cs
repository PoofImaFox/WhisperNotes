using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoteScribe.Cli.Rendering;
using NoteScribe.Core.Composition;
using NoteScribe.Core.Notes;

namespace NoteScribe.Cli.Commands;

/// <summary>Lists past sessions from the notes tree.</summary>
internal static class SessionsCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly Option<string> ProjectOption = new("--project", "-p")
    {
        Description = "Filter to one project.",
        HelpName = "name"
    };

    private static readonly Option<string> SinceOption = new("--since")
    {
        Description = "Lower bound: yyyy-MM-dd, or relative like 7d / 12h / 2w.",
        HelpName = "date"
    };

    private static readonly Option<string> UntilOption = new("--until")
    {
        Description = "Upper bound: yyyy-MM-dd (inclusive), or relative like 7d / 12h / 2w.",
        HelpName = "date"
    };

    private static readonly Option<string> SearchOption = new("--search", "-s")
    {
        Description = "Match against title and transcript text.",
        HelpName = "text"
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Machine-readable output."
    };

    public static Command Create()
    {
        Command command = new("sessions", "List past sessions from the notes tree.");

        command.Options.Add(ProjectOption);
        command.Options.Add(SinceOption);
        command.Options.Add(UntilOption);
        command.Options.Add(SearchOption);
        command.Options.Add(JsonOption);

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, environment => ExecuteAsync(parseResult, environment, cancellationToken)));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        CliEnvironment environment,
        CancellationToken cancellationToken)
    {
        ConsoleOutput console = environment.Console;

        NoteQuery query = new(
            parseResult.GetValue(ProjectOption),
            ParseBound(parseResult.GetValue(SinceOption), "--since", inclusiveEndOfDay: false),
            ParseBound(parseResult.GetValue(UntilOption), "--until", inclusiveEndOfDay: true),
            parseResult.GetValue(SearchOption));

        await using NoteScribeServices services = NoteScribeServices.Create(environment.Settings);

        IReadOnlyList<NoteSession> sessions = await services.Notes
            .ListSessionsAsync(query, cancellationToken)
            .ConfigureAwait(false);

        List<Row> rows = [];
        foreach (NoteSession session in sessions)
        {
            IReadOnlyList<NoteEntry> entries = await services.Notes
                .LoadEntriesAsync(session.Id, cancellationToken)
                .ConfigureAwait(false);

            var directory = services.Notes.GetSessionDirectory(session.Id);
            rows.Add(new Row(session, entries.Count, directory));
        }

        if (parseResult.GetValue(JsonOption))
        {
            console.Result(JsonSerializer.Serialize(rows.Select(Row.ToJson), JsonOptions));
            return ExitCode.Success;
        }

        Print(console, rows, services.Notes.RootDirectory);
        return ExitCode.Success;
    }

    private static void Print(ConsoleOutput console, List<Row> rows, string root)
    {
        console.Result();

        if (rows.Count == 0)
        {
            console.Result($"  No sessions matched under {root}.");
            console.Result();
            return;
        }

        var projectWidth = Math.Max(7, rows.Max(r => Project(r.Session).Length));
        var titleWidth = Math.Max(5, rows.Max(r => r.Session.Title.Length));

        foreach (Row row in rows)
        {
            var started = row.Session.StartedUtc.ToLocalTime();
            var duration = row.Session.EndedUtc is null ? "  active" : Format.Clock(row.Session.Duration);

            console.Result(string.Format(
                CultureInfo.InvariantCulture,
                "  {0:yyyy-MM-dd HH:mm}  {1}  {2}  {3}  {4}",
                started,
                Project(row.Session).PadRight(projectWidth),
                row.Session.Title.PadRight(titleWidth),
                duration.PadLeft(8),
                Format.Count(row.Entries, "entry", "entries").PadLeft(11)));

            console.Result("      " + Path.Combine(row.Directory, FileSystemNoteRepository.NotesFileName));
        }

        console.Result();
        console.Result($"  {Format.Count(rows.Count, "session", "sessions")} under {root}");
        console.Result();
    }

    private static string Project(NoteSession session) => session.Project ?? FileSystemNoteRepository.UnfiledProject;

    /// <summary>
    /// Accepts <c>yyyy-MM-dd</c> and relative offsets like <c>7d</c>. Dates are read as local time
    /// because that is what the folder tree is named after.
    /// </summary>
    private static DateTimeOffset? ParseBound(string? value, string option, bool inclusiveEndOfDay)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        if (TryParseRelative(value, out TimeSpan back))
        {
            return DateTimeOffset.Now - back;
        }

        if (DateTime.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            // NoteQuery.To is exclusive, so "--until 2026-07-25" means "through the 25th".
            return new DateTimeOffset(inclusiveEndOfDay ? date.AddDays(1) : date, DateTimeOffset.Now.Offset);
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw new CliException(
            ExitCode.Usage,
            $"{option} could not read '{value}'. Use yyyy-MM-dd, or a relative offset like 7d, 12h or 2w.");
    }

    private static bool TryParseRelative(string value, out TimeSpan span)
    {
        span = default;

        if (value.Length < 2)
        {
            return false;
        }

        var unit = char.ToLowerInvariant(value[^1]);
        if (!double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount < 0)
        {
            return false;
        }

        span = unit switch
        {
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(amount * 7),
            _ => TimeSpan.Zero
        };

        return span != TimeSpan.Zero || amount == 0 && unit is 'h' or 'd' or 'w';
    }

    private sealed record Row(NoteSession Session, int Entries, string Directory)
    {
        public static object ToJson(Row row) => new
        {
            row.Session.Id,
            row.Session.Title,
            Project = row.Session.Project,
            StartedUtc = row.Session.StartedUtc,
            StartedLocal = row.Session.StartedUtc.ToLocalTime(),
            EndedUtc = row.Session.EndedUtc,
            DurationSeconds = Math.Round(row.Session.Duration.TotalSeconds, 3),
            Active = row.Session.IsActive,
            row.Session.SourceDescription,
            row.Session.Tags,
            row.Session.ModelUsed,
            EntryCount = row.Entries,
            Directory = row.Directory,
            NotesPath = Path.Combine(row.Directory, FileSystemNoteRepository.NotesFileName),
            TranscriptPath = Path.Combine(row.Directory, FileSystemNoteRepository.TranscriptFileName)
        };
    }
}

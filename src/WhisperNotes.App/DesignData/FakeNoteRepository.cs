using System.Globalization;
using System.Text;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.App.DesignData;

/// <summary>
/// In-memory stand-in for the filesystem repository. It mirrors the real on-disk layout for
/// <see cref="GetSessionDirectory"/> and does write a real markdown file on finalize, so the
/// "here is where your notes landed" flow can actually be clicked through without Core.
/// </summary>
internal sealed class FakeNoteRepository : INoteRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NoteSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NoteEntry>> _entries = new(StringComparer.Ordinal);

    public FakeNoteRepository()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "WhisperNotes-SampleNotes");

        foreach (var session in SampleData.Sessions)
        {
            _sessions[session.Id] = session;
            _entries[session.Id] = [.. SampleData.Entries[session.Id]];
        }
    }

    public string RootDirectory { get; }

    public Task<NoteSession> CreateSessionAsync(
        string title,
        string? project,
        string sourceDescription,
        IReadOnlyList<string> tags,
        string? modelUsed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var started = DateTimeOffset.Now;
        var id = $"{started:yyyyMMdd-HHmmss}-{Slug(title)}";
        var session = new NoteSession(id, title, project, started, null, sourceDescription, tags, modelUsed);

        lock (_gate)
        {
            _sessions[id] = session;
            _entries[id] = [];
        }

        return Task.FromResult(session);
    }

    public Task AppendEntryAsync(string sessionId, NoteEntry entry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var list))
            {
                throw new InvalidOperationException($"Unknown session '{sessionId}'.");
            }

            list.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task UpdateEntryAsync(string sessionId, NoteEntry entry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var list))
            {
                throw new InvalidOperationException($"Unknown session '{sessionId}'.");
            }

            var index = list.FindIndex(e => string.Equals(e.Id, entry.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException($"Unknown entry '{entry.Id}'.");
            }

            list[index] = entry;
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<NoteSession>> ListSessionsAsync(NoteQuery query, CancellationToken cancellationToken)
    {
        // Real listing walks the notes tree; the small delay keeps callers honest about awaiting it.
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return _sessions.Values
                .Where(s => query.Project is null || string.Equals(s.Project, query.Project, StringComparison.OrdinalIgnoreCase))
                .Where(s => query.From is null || s.StartedUtc >= query.From)
                .Where(s => query.To is null || s.StartedUtc < query.To)
                .Where(s => Matches(s, query.TextContains))
                .OrderByDescending(s => s.StartedUtc)
                .ToList();
        }
    }

    public async Task<IReadOnlyList<NoteEntry>> LoadEntriesAsync(string sessionId, CancellationToken cancellationToken)
    {
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return _entries.TryGetValue(sessionId, out var list) ? [.. list] : [];
        }
    }

    public Task RerenderAsync(string sessionId, CancellationToken cancellationToken)
    {
        NoteSession session;
        List<NoteEntry> entries;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var existing))
            {
                throw new InvalidOperationException($"Unknown session '{sessionId}'.");
            }

            session = existing;
            entries = [.. _entries[sessionId]];
        }

        var directory = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(directory);
        return File.WriteAllTextAsync(Path.Combine(directory, "notes.md"), Render(session, entries), cancellationToken);
    }

    public async Task<NoteSession> FinalizeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken,
        TimeSpan? contentDuration = null)
    {
        NoteSession session;
        List<NoteEntry> entries;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var existing))
            {
                throw new InvalidOperationException($"Unknown session '{sessionId}'.");
            }

            session = existing with { EndedUtc = DateTimeOffset.Now };
            _sessions[sessionId] = session;
            entries = [.. _entries[sessionId]];
        }

        var directory = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "notes.md"), Render(session, entries), cancellationToken)
            .ConfigureAwait(false);

        return session;
    }

    public string GetSessionDirectory(string sessionId)
    {
        NoteSession? session;
        lock (_gate)
        {
            _sessions.TryGetValue(sessionId, out session);
        }

        if (session is null)
        {
            return RootDirectory;
        }

        var started = session.StartedUtc;
        return Path.Combine(
            RootDirectory,
            string.IsNullOrWhiteSpace(session.Project) ? "_unfiled" : Sanitize(session.Project),
            started.ToString("yyyy", CultureInfo.InvariantCulture),
            started.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            $"{started:HHmmss}-{Slug(session.Title)}");
    }

    private bool Matches(NoteSession session, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (session.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (session.Project?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
            || session.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return _entries.TryGetValue(session.Id, out var list)
            && list.Any(e => e.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static string Render(NoteSession session, IReadOnlyList<NoteEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(session.Title);
        sb.AppendLine();
        sb.Append("- Project: ").AppendLine(session.Project ?? "_unfiled");
        sb.Append("- Started: ").AppendLine(session.StartedUtc.ToString("u", CultureInfo.InvariantCulture));
        sb.Append("- Duration: ").AppendLine(session.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        sb.Append("- Source: ").AppendLine(session.SourceDescription);
        sb.AppendLine();

        foreach (var entry in entries)
        {
            var stamp = entry.Offset.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            var prefix = entry.Kind switch
            {
                NoteEntryKind.ActionItem => "- [ ] ",
                NoteEntryKind.Marker => "> ",
                _ => string.Empty
            };
            sb.Append('`').Append(stamp).Append("` ").Append(prefix).AppendLine(entry.Text);
        }

        return sb.ToString();
    }

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return slug.Length == 0 ? "session" : slug[..Math.Min(48, slug.Length)];
    }

    private static string Sanitize(string value) =>
        new([.. value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
}

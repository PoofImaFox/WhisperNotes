using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WhisperNotes.Core.Notes;

/// <summary>
/// Stores sessions as <c>&lt;root&gt;/&lt;project&gt;/&lt;yyyy&gt;/&lt;yyyy-MM-dd&gt;/&lt;HHmmss&gt;-&lt;slug&gt;/</c>
/// with a rewritten <c>session.json</c>, an append-only <c>transcript.jsonl</c> and a rendered
/// <c>notes.md</c>.
/// </summary>
public sealed class FileSystemNoteRepository : INoteRepository, IAsyncDisposable
{
    public const string UnfiledProject = "_unfiled";
    public const string SessionFileName = "session.json";
    public const string TranscriptFileName = "transcript.jsonl";
    public const string NotesFileName = "notes.md";
    public const string AudioDirectoryName = "audio";

    private const int MaxSlugLength = 40;

    private readonly INoteExporter _exporter;
    private readonly ConcurrentDictionary<string, string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SessionWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _writerLock = new();

    public FileSystemNoteRepository(string rootDirectory, INoteExporter? exporter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        _exporter = exporter ?? new MarkdownNoteExporter();
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>
    /// Canonical serialization settings for every file this app writes. Compact by design so a
    /// <see cref="NoteEntry"/> always fits on one jsonl line; <see cref="IndentedJsonOptions"/>
    /// is the same shape for the human-edited <c>session.json</c>.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions(indented: false);

    public static JsonSerializerOptions IndentedJsonOptions { get; } = CreateOptions(indented: true);

    public string RootDirectory { get; }

    public async Task<NoteSession> CreateSessionAsync(
        string title,
        string? project,
        string sourceDescription,
        IReadOnlyList<string> tags,
        string? modelUsed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startedUtc = DateTimeOffset.UtcNow;
        // Folder names use local time: the tree exists to be browsed by a human in Explorer.
        var local = startedUtc.ToLocalTime();
        var effectiveTitle = string.IsNullOrWhiteSpace(title)
            ? local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : title.Trim();

        var dateDirectory = Path.Combine(
            RootDirectory,
            SanitiseProjectFolder(project),
            local.ToString("yyyy", CultureInfo.InvariantCulture),
            local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(dateDirectory);

        var baseName = $"{local:HHmmss}-{Slugify(effectiveTitle)}";
        var folderName = baseName;
        for (var suffix = 2; Directory.Exists(Path.Combine(dateDirectory, folderName)) || _directories.ContainsKey(folderName); suffix++)
        {
            folderName = $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        var sessionDirectory = Path.Combine(dateDirectory, folderName);
        Directory.CreateDirectory(sessionDirectory);

        var session = new NoteSession(
            folderName,
            effectiveTitle,
            string.IsNullOrWhiteSpace(project) ? null : project.Trim(),
            startedUtc,
            EndedUtc: null,
            sourceDescription ?? string.Empty,
            tags is null ? [] : [.. tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())],
            modelUsed);

        _directories[session.Id] = sessionDirectory;
        await WriteSessionFileAsync(sessionDirectory, session, cancellationToken).ConfigureAwait(false);

        var transcript = Path.Combine(sessionDirectory, TranscriptFileName);
        if (!File.Exists(transcript))
        {
            await File.WriteAllTextAsync(transcript, string.Empty, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    public async Task AppendEntryAsync(string sessionId, NoteEntry entry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var writer = GetWriter(sessionId);
        var line = JsonSerializer.Serialize(entry, JsonOptions);

        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.Stream.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Gate.Release();
        }
    }

    /// <summary>
    /// The jsonl is append-only so an edit is written as a *new* record carrying the same
    /// <see cref="NoteEntry.Id"/>. <see cref="LoadEntriesAsync"/> keeps the last record per id
    /// (last-write-wins) while preserving the position of the original, so an edit can never
    /// corrupt earlier content and a crash mid-write costs at most the edit itself.
    /// </summary>
    public Task UpdateEntryAsync(string sessionId, NoteEntry entry, CancellationToken cancellationToken) =>
        AppendEntryAsync(sessionId, entry, cancellationToken);

    public async Task<IReadOnlyList<NoteSession>> ListSessionsAsync(NoteQuery query, CancellationToken cancellationToken)
    {
        query ??= new NoteQuery();
        var matches = new List<NoteSession>();

        foreach (var sessionFile in EnumerateSessionFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = await TryReadSessionAsync(sessionFile, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(sessionFile);
            if (!string.IsNullOrEmpty(directory))
            {
                _directories[session.Id] = directory;
            }

            if (query.Project is { Length: > 0 } project &&
                !string.Equals(session.Project ?? UnfiledProject, project, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.From is { } from && session.StartedUtc < from)
            {
                continue;
            }

            if (query.To is { } to && session.StartedUtc >= to)
            {
                continue;
            }

            if (query.TextContains is { Length: > 0 } text &&
                !await MatchesTextAsync(session, directory, text, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            matches.Add(session);
        }

        matches.Sort(static (a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
        return matches;
    }

    public async Task<IReadOnlyList<NoteEntry>> LoadEntriesAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await ReadEntriesAsync(
            Path.Combine(GetSessionDirectory(sessionId), TranscriptFileName),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RerenderAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var directory = GetSessionDirectory(sessionId);
        var session = await ReadSessionOrThrowAsync(directory, sessionId, cancellationToken).ConfigureAwait(false);

        // Deliberately no CloseWriterAsync and no EndedUtc stamp: a re-render is a read of the
        // session, not the end of it, and it has to be callable mid-recording.
        await RenderNotesAsync(directory, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NoteSession> FinalizeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken,
        TimeSpan? contentDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var directory = GetSessionDirectory(sessionId);
        await CloseWriterAsync(sessionId).ConfigureAwait(false);

        var session = await ReadSessionOrThrowAsync(directory, sessionId, cancellationToken).ConfigureAwait(false);

        var end = contentDuration is { } span
            ? session.StartedUtc + span
            : session.EndedUtc ?? DateTimeOffset.UtcNow;

        var finalized = session with { EndedUtc = end };
        await WriteSessionFileAsync(directory, finalized, cancellationToken).ConfigureAwait(false);
        await RenderNotesAsync(directory, finalized, cancellationToken).ConfigureAwait(false);

        return finalized;
    }

    public string GetSessionDirectory(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_directories.TryGetValue(sessionId, out var cached) && Directory.Exists(cached))
        {
            return cached;
        }

        foreach (var sessionFile in EnumerateSessionFiles())
        {
            var directory = Path.GetDirectoryName(sessionFile);
            if (directory is null)
            {
                continue;
            }

            var id = ReadSessionId(sessionFile) ?? Path.GetFileName(directory);
            _directories[id] = directory;

            if (string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException($"No session '{sessionId}' under {RootDirectory}.");
    }

    /// <summary>Folder the session's audio copy belongs in when KeepSessionAudio is on.</summary>
    public string GetSessionAudioDirectory(string sessionId)
    {
        var directory = Path.Combine(GetSessionDirectory(sessionId), AudioDirectoryName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _writers.Keys.ToArray())
        {
            await CloseWriterAsync(id).ConfigureAwait(false);
        }
    }

    internal static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "session";
        }

        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        var slug = builder.ToString();
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "session" : slug;
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter(),
            new TimeSpanConverter()
        },
        // NoteSession exposes computed getters (IsActive, Duration). Persisting those would bake a
        // stale answer into session.json, so drop anything the deserializer could not set back.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static typeInfo =>
                {
                    if (typeInfo.Kind != JsonTypeInfoKind.Object)
                    {
                        return;
                    }

                    for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
                    {
                        if (typeInfo.Properties[i].Set is null)
                        {
                            typeInfo.Properties.RemoveAt(i);
                        }
                    }
                }
            }
        }
    };

    private static string SanitiseProjectFolder(string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return UnfiledProject;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(project.Length);
        foreach (var c in project.Trim())
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);
        }

        var name = builder.ToString().Trim(' ', '.');
        return name.Length == 0 ? UnfiledProject : name;
    }

    private IEnumerable<string> EnumerateSessionFiles()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(RootDirectory, SessionFileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadSessionId(string sessionFile)
    {
        try
        {
            using var stream = File.OpenRead(sessionFile);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<NoteSession?> TryReadSessionAsync(string sessionFile, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(sessionFile, cancellationToken).ConfigureAwait(false);
            var session = JsonSerializer.Deserialize<NoteSession>(json, JsonOptions);
            return session is null
                ? null
                : session with { Tags = session.Tags ?? [] };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<NoteSession> ReadSessionOrThrowAsync(
        string directory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var sessionFile = Path.Combine(directory, SessionFileName);

        return await TryReadSessionAsync(sessionFile, cancellationToken).ConfigureAwait(false)
               ?? throw new FileNotFoundException($"Session '{sessionId}' has no {SessionFileName}.", sessionFile);
    }

    /// <summary>
    /// The one place <c>notes.md</c> is produced, so finalize and re-render can never drift into
    /// rendering the same session two different ways.
    /// </summary>
    private async Task RenderNotesAsync(string directory, NoteSession session, CancellationToken cancellationToken)
    {
        var entries = await ReadEntriesAsync(Path.Combine(directory, TranscriptFileName), cancellationToken)
            .ConfigureAwait(false);
        var markdown = _exporter.Render(session, entries);
        await WriteAtomicAsync(Path.Combine(directory, NotesFileName), markdown, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> MatchesTextAsync(
        NoteSession session,
        string? directory,
        string text,
        CancellationToken cancellationToken)
    {
        if (session.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var entries = await ReadEntriesAsync(Path.Combine(directory, TranscriptFileName), cancellationToken)
            .ConfigureAwait(false);

        return entries.Any(e => e.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<NoteEntry>> ReadEntriesAsync(string transcriptPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(transcriptPath))
        {
            return [];
        }

        // Flush anything still buffered for an active session so readers see the whole log.
        var sessionId = Path.GetFileName(Path.GetDirectoryName(transcriptPath));
        if (sessionId is not null && _writers.TryGetValue(sessionId, out var live))
        {
            await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await live.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                live.Gate.Release();
            }
        }

        var order = new List<string>();
        var byId = new Dictionary<string, NoteEntry>(StringComparer.Ordinal);

        await using var stream = new FileStream(
            transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0 || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            NoteEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<NoteEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // A torn final line from a crash — everything before it is still good.
            }

            if (entry is null || string.IsNullOrEmpty(entry.Id))
            {
                continue;
            }

            if (!byId.ContainsKey(entry.Id))
            {
                order.Add(entry.Id);
            }

            byId[entry.Id] = entry;
        }

        var results = new List<NoteEntry>(order.Count);
        foreach (var id in order)
        {
            results.Add(byId[id]);
        }

        return results;
    }

    private SessionWriter GetWriter(string sessionId)
    {
        if (_writers.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        lock (_writerLock)
        {
            if (_writers.TryGetValue(sessionId, out existing))
            {
                return existing;
            }

            var created = new SessionWriter(Path.Combine(GetSessionDirectory(sessionId), TranscriptFileName));
            _writers[sessionId] = created;
            return created;
        }
    }

    private async Task CloseWriterAsync(string sessionId)
    {
        SessionWriter? writer;
        lock (_writerLock)
        {
            if (!_writers.TryRemove(sessionId, out writer))
            {
                return;
            }
        }

        await writer.DisposeAsync().ConfigureAwait(false);
    }

    private static Task WriteSessionFileAsync(string directory, NoteSession session, CancellationToken cancellationToken) =>
        WriteAtomicAsync(
            Path.Combine(directory, SessionFileName),
            JsonSerializer.Serialize(session, IndentedJsonOptions),
            cancellationToken);

    // Write to a sibling temp file and move it over the target: a crash mid-write leaves the
    // previous good file in place instead of a half-written one.
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
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
            // Best effort.
        }
    }

    /// <summary>One open append handle per active session, guarded so dictation-rate writes never interleave.</summary>
    private sealed class SessionWriter : IAsyncDisposable
    {
        public SessionWriter(string transcriptPath)
        {
            var directory = Path.GetDirectoryName(transcriptPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // ReadWrite sharing so the live transcript can still be tailed from outside the app.
            var stream = new FileStream(
                transcriptPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);

            Stream = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public StreamWriter Stream { get; }

        public async ValueTask DisposeAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Stream.FlushAsync().ConfigureAwait(false);
                await Stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
                Gate.Dispose();
            }
        }
    }

    /// <summary>System.Text.Json has no built-in TimeSpan support; store the round-trippable "c" form.</summary>
    private sealed class TimeSpanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return TimeSpan.FromSeconds(reader.GetDouble());
            }

            var text = reader.GetString();
            return string.IsNullOrWhiteSpace(text)
                ? TimeSpan.Zero
                : TimeSpan.Parse(text, CultureInfo.InvariantCulture);
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
    }
}

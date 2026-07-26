using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WhisperNotes.Core.Notes.Documents;

/// <summary>
/// Stores documents as <c>&lt;notesRoot&gt;/_documents/&lt;slug&gt;-&lt;shortid&gt;/</c> holding a
/// <c>document.json</c> (metadata only), a <c>content.md</c> (the live body) and a
/// <c>revisions/</c> folder of pre-change snapshots.
/// </summary>
/// <remarks>
/// <para>
/// This is a sibling of <see cref="FileSystemNoteRepository"/> and shares its conventions: the same
/// JSON options, atomic temp-file writes, UTF-8 without BOM, <c>\n</c> newlines, a semaphore per
/// document to serialise writers, and a directory cache with a rescan fallback.
/// </para>
/// <para>
/// Everything lives under a single <c>_documents/</c> folder so the project/year/date session tree
/// stays untouched and <see cref="FileSystemNoteRepository.ListSessionsAsync"/>'s recursive
/// <c>session.json</c> scan can never pick a document up.
/// </para>
/// <para>
/// Write ordering is chosen so a crash cannot lose text: the revision holding the previous body is
/// flushed <em>before</em> <c>content.md</c> is replaced. The worst a crash can do is leave a
/// revision whose content equals the head.
/// </para>
/// </remarks>
public sealed class FileSystemNoteDocumentStore : INoteDocumentStore, IAsyncDisposable
{
    public const string DocumentsDirectoryName = "_documents";
    public const string DocumentFileName = "document.json";
    public const string ContentFileName = "content.md";
    public const string RevisionsDirectoryName = "revisions";

    /// <summary>Ceiling on retained revisions per document. The oldest is never pruned.</summary>
    public const int MaxRevisionsPerDocument = 200;

    private const int MaxSlugLength = 40;
    private const string FallbackSlug = "untitled";
    private const string FallbackTitle = "Untitled";
    private const string FallbackLabel = "Edit";

    // Sortable to 100ns so two saves in the same millisecond still order correctly by file name.
    private const string RevisionStampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";
    private const int RevisionStampLength = 23;

    private readonly ConcurrentDictionary<string, string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DocumentWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _writerLock = new();
    private bool _disposed;

    /// <param name="notesRoot">
    /// <c>AppSettings.NotesRoot</c>. The store owns the <c>_documents/</c> subfolder of it and
    /// nothing else.
    /// </param>
    public FileSystemNoteDocumentStore(string notesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesRoot);
        NotesRoot = Path.GetFullPath(notesRoot);
        RootDirectory = Path.Combine(NotesRoot, DocumentsDirectoryName);
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>The notes tree this store was pointed at — the parent of <see cref="RootDirectory"/>.</summary>
    public string NotesRoot { get; }

    /// <summary><c>&lt;NotesRoot&gt;/_documents</c>. Every document folder is a direct child.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Lists documents newest-updated first.
    /// </summary>
    /// <remarks>
    /// The returned documents carry their full <see cref="NoteDocument.Content"/>, so a list entry
    /// is safe to hand straight to <see cref="SaveAsync"/> without re-loading it first. That costs
    /// one <c>content.md</c> read per document; the alternative — returning entries with an empty
    /// body — turns an innocent list-then-save into data loss, which loses to "the user can always
    /// get their text back". The cheap part is the matching: title, project and tags are tested
    /// first and the body is only scanned for the documents those miss.
    /// </remarks>
    /// <param name="search">
    /// Null/blank returns everything. Otherwise a case-insensitive match over title, project, tags
    /// and body.
    /// </param>
    public async Task<IReadOnlyList<NoteDocument>> ListAsync(string? search, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var matches = new List<NoteDocument>();

        foreach (var directory in EnumerateDocumentDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            _directories[id] = directory;

            // A null here means document.json is missing or torn. That must not hide the body.
            var metadata = await TryReadMetadataAsync(Path.Combine(directory, DocumentFileName), cancellationToken)
                .ConfigureAwait(false);

            var body = await ReadContentAsync(directory, cancellationToken).ConfigureAwait(false);

            // Short-circuits: a title/project/tag hit never pays for a scan of the body.
            if (term is not null &&
                !MatchesMetadata(metadata, term) &&
                !body.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(Compose(id, directory, metadata, body));
        }

        matches.Sort(static (a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
        return matches;
    }

    public async Task<NoteDocument?> LoadAsync(string documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = TryGetDocumentDirectory(documentId);
        return directory is null
            ? null
            : await LoadCoreAsync(documentId, directory, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NoteDocument> CreateAsync(
        string title,
        string? project,
        string content,
        string? sourceSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(RootDirectory);

        var now = DateTimeOffset.UtcNow;
        var body = NormalizeNewLines(content);

        var baseName = $"{Slugify(title)}-{NewShortId()}";
        var folderName = baseName;
        for (var suffix = 2;
             Directory.Exists(Path.Combine(RootDirectory, folderName)) || _directories.ContainsKey(folderName);
             suffix++)
        {
            folderName = $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        var directory = Path.Combine(RootDirectory, folderName);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, RevisionsDirectoryName));

        var document = new NoteDocument(
            folderName,
            string.IsNullOrWhiteSpace(title) ? FallbackTitle : title.Trim(),
            NormalizeProject(project),
            body,
            now,
            now,
            [],
            string.IsNullOrWhiteSpace(sourceSessionId) ? null : sourceSessionId.Trim());

        _directories[folderName] = directory;

        var writer = GetWriter(folderName);
        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // No revision on create: the first SaveAsync captures this body as revision zero, and
            // writing one here would duplicate it.
            await WriteAtomicAsync(Path.Combine(directory, ContentFileName), body, cancellationToken)
                .ConfigureAwait(false);
            await WriteMetadataAsync(directory, document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Gate.Release();
        }

        return document;
    }

    public async Task<NoteDocument> SaveAsync(
        NoteDocument document,
        string revisionLabel,
        string origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var directory = GetDocumentDirectory(document.Id);
        var writer = GetWriter(document.Id);

        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WriteHeadAsync(
                document.Id,
                directory,
                document,
                NormalizeNewLines(document.Content),
                revisionLabel,
                origin,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Gate.Release();
        }
    }

    public async Task<NoteDocument> RenameAsync(string documentId, string newTitle, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var directory = GetDocumentDirectory(documentId);
        var writer = GetWriter(documentId);

        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(documentId, directory, cancellationToken).ConfigureAwait(false);
            var renamed = current with
            {
                Title = string.IsNullOrWhiteSpace(newTitle) ? FallbackTitle : newTitle.Trim(),
                UpdatedUtc = DateTimeOffset.UtcNow
            };

            // The folder name is the id, so a rename never moves anything on disk.
            await WriteMetadataAsync(directory, renamed, cancellationToken).ConfigureAwait(false);
            return renamed;
        }
        finally
        {
            writer.Gate.Release();
        }
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = TryGetDocumentDirectory(documentId);
        await CloseWriterAsync(documentId).ConfigureAwait(false);
        _directories.TryRemove(documentId, out _);

        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Someone else got there first.
        }
    }

    /// <summary>Revisions oldest first — index zero is the original body.</summary>
    public async Task<IReadOnlyList<NoteRevision>> ListRevisionsAsync(string documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = TryGetDocumentDirectory(documentId);
        if (directory is null)
        {
            return [];
        }

        var results = new List<NoteRevision>();
        foreach (var file in EnumerateRevisionFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var revision = await TryReadRevisionAsync(file, documentId, cancellationToken).ConfigureAwait(false);
            if (revision is not null)
            {
                // A torn snapshot costs that snapshot, never the whole history.
                results.Add(revision);
            }
        }

        return results;
    }

    public async Task<NoteRevision?> LoadRevisionAsync(string documentId, string revisionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = TryGetDocumentDirectory(documentId);
        return directory is null
            ? null
            : await TryLoadRevisionAsync(directory, documentId, revisionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NoteDocument> RevertAsync(string documentId, string revisionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var directory = GetDocumentDirectory(documentId);
        var writer = GetWriter(documentId);

        await writer.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var revision = await TryLoadRevisionAsync(directory, documentId, revisionId, cancellationToken)
                               .ConfigureAwait(false)
                           ?? throw new FileNotFoundException(
                               $"Document '{documentId}' has no revision '{revisionId}'.",
                               Path.Combine(directory, RevisionsDirectoryName, revisionId + ".json"));

            var current = await LoadCoreAsync(documentId, directory, cancellationToken).ConfigureAwait(false);

            var label = string.Create(
                CultureInfo.InvariantCulture,
                $"Reverted to {revision.TimestampUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");

            // Routed through the normal head write, so the state being replaced becomes a revision
            // of its own: the revert is itself undoable.
            return await WriteHeadAsync(
                documentId,
                directory,
                current,
                NormalizeNewLines(revision.Content),
                label,
                NoteRevisionOrigin.User,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Gate.Release();
        }
    }

    public string GetDocumentDirectory(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        return TryGetDocumentDirectory(documentId)
               ?? throw new DirectoryNotFoundException($"No note document '{documentId}' under {RootDirectory}.");
    }

    /// <summary>
    /// Waits for every in-flight write to complete and releases the per-document semaphores. Writes
    /// are atomic and fully flushed before the gate is released, so there is nothing buffered to
    /// lose here.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var id in _writers.Keys.ToArray())
        {
            await CloseWriterAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deliberate duplicate of <c>FileSystemNoteRepository.Slugify</c>: that one is
    /// <c>internal static</c> but falls back to "session", and this store may not edit it to
    /// parameterise the fallback. Same rules otherwise — lowercase alphanumerics, '-' separated,
    /// capped at 40 characters.
    /// </summary>
    internal static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FallbackSlug;
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

        return slug.Length == 0 ? FallbackSlug : slug;
    }

    /// <summary>
    /// Collapses <c>\r\n</c> and lone <c>\r</c> to <c>\n</c> so the on-disk body matches the
    /// repository-wide newline convention, "unchanged" comparisons are stable no matter which
    /// editor produced the text, and diffs never show a whole-file change after a round trip.
    /// </summary>
    internal static string NormalizeNewLines(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains('\r', StringComparison.Ordinal)
            ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : value;
    }

    // ---- head writes -------------------------------------------------------------------------

    /// <summary>Caller must hold the document's gate.</summary>
    private static async Task<NoteDocument> WriteHeadAsync(
        string documentId,
        string directory,
        NoteDocument requested,
        string content,
        string revisionLabel,
        string origin,
        CancellationToken cancellationToken)
    {
        var previous = await ReadContentAsync(directory, cancellationToken).ConfigureAwait(false);
        var existing = await TryReadMetadataAsync(Path.Combine(directory, DocumentFileName), cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (!string.Equals(previous, content, StringComparison.Ordinal))
        {
            // Snapshot first, replace second. A crash between the two costs a redundant revision,
            // never the previous text.
            await WriteRevisionAsync(directory, documentId, previous, revisionLabel, origin, now, cancellationToken)
                .ConfigureAwait(false);
            PruneRevisions(directory);
            await WriteAtomicAsync(Path.Combine(directory, ContentFileName), content, cancellationToken)
                .ConfigureAwait(false);
        }

        var created = existing?.CreatedUtc
                      ?? (requested.CreatedUtc == default ? now : requested.CreatedUtc);

        var title = string.IsNullOrWhiteSpace(requested.Title)
            ? existing?.Title ?? FallbackTitle
            : requested.Title.Trim();

        var saved = new NoteDocument(
            documentId,
            string.IsNullOrWhiteSpace(title) ? FallbackTitle : title,
            NormalizeProject(requested.Project),
            content,
            created,
            now,
            NormalizeTags(requested.Tags),
            string.IsNullOrWhiteSpace(requested.SourceSessionId)
                ? existing?.SourceSessionId
                : requested.SourceSessionId.Trim());

        await WriteMetadataAsync(directory, saved, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    private static async Task<NoteRevision> WriteRevisionAsync(
        string directory,
        string documentId,
        string content,
        string label,
        string origin,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var revisions = Path.Combine(directory, RevisionsDirectoryName);
        Directory.CreateDirectory(revisions);

        // File names are the sort key for history, so never let a coarse clock emit one that sorts
        // at or before the newest snapshot already on disk.
        var stamp = timestamp.UtcDateTime;
        if (TryReadNewestStamp(directory) is { } newest && stamp <= newest)
        {
            stamp = newest.AddTicks(1);
        }

        var revisionId = $"{stamp.ToString(RevisionStampFormat, CultureInfo.InvariantCulture)}-{NewShortId()}";
        var revision = new NoteRevision(
            revisionId,
            documentId,
            new DateTimeOffset(stamp, TimeSpan.Zero),
            string.IsNullOrWhiteSpace(label) ? FallbackLabel : label.Trim(),
            string.IsNullOrWhiteSpace(origin) ? NoteRevisionOrigin.User : origin.Trim(),
            content);

        await WriteAtomicAsync(
            Path.Combine(revisions, revisionId + ".json"),
            JsonSerializer.Serialize(revision, FileSystemNoteRepository.IndentedJsonOptions),
            cancellationToken).ConfigureAwait(false);

        return revision;
    }

    /// <summary>
    /// Trims history to <see cref="MaxRevisionsPerDocument"/>, oldest first, but keeps index zero
    /// (the original) and the newest entry forever.
    /// </summary>
    private static void PruneRevisions(string directory)
    {
        var files = EnumerateRevisionFiles(directory);
        if (files.Count <= MaxRevisionsPerDocument)
        {
            return;
        }

        var last = Math.Min(files.Count - MaxRevisionsPerDocument, files.Count - 2);
        for (var i = 1; i <= last; i++)
        {
            TryDelete(files[i]);
        }
    }

    // ---- reads -------------------------------------------------------------------------------

    private static async Task<NoteDocument> LoadCoreAsync(string documentId, string directory, CancellationToken cancellationToken)
    {
        var metadata = await TryReadMetadataAsync(Path.Combine(directory, DocumentFileName), cancellationToken)
            .ConfigureAwait(false);
        var content = await ReadContentAsync(directory, cancellationToken).ConfigureAwait(false);
        return Compose(documentId, directory, metadata, content);
    }

    /// <summary>
    /// Rehydrates a full document from its metadata plus the body read out of <c>content.md</c>.
    /// Unreadable metadata degrades to the folder name and file timestamps rather than throwing.
    /// </summary>
    private static NoteDocument Compose(string id, string directory, DocumentMetadata? metadata, string content)
    {
        var fallback = metadata is null ? GetFallbackTimestamp(directory) : default;

        return new NoteDocument(
            id,
            string.IsNullOrWhiteSpace(metadata?.Title) ? id : metadata!.Title,
            NormalizeProject(metadata?.Project),
            content,
            metadata?.CreatedUtc ?? fallback,
            metadata?.UpdatedUtc ?? fallback,
            NormalizeTags(metadata?.Tags),
            metadata?.SourceSessionId);
    }

    private static async Task<string> ReadContentAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, ContentFileName);
        if (!File.Exists(path))
        {
            return string.Empty; // A document with no body yet is empty, not broken.
        }

        var text = await TryReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return NormalizeNewLines(text);
    }

    private static async Task<DocumentMetadata?> TryReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var json = await TryReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DocumentMetadata>(json, FileSystemNoteRepository.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<NoteRevision?> TryReadRevisionAsync(string path, string documentId, CancellationToken cancellationToken)
    {
        var json = await TryReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        NoteRevision? revision;
        try
        {
            revision = JsonSerializer.Deserialize<NoteRevision>(json, FileSystemNoteRepository.JsonOptions);
        }
        catch (JsonException)
        {
            return null; // A torn snapshot from a crash — every other snapshot is still good.
        }

        if (revision is null)
        {
            return null;
        }

        var id = string.IsNullOrWhiteSpace(revision.Id)
            ? Path.GetFileNameWithoutExtension(path)
            : revision.Id;

        return revision with
        {
            Id = id,
            DocumentId = string.IsNullOrWhiteSpace(revision.DocumentId) ? documentId : revision.DocumentId,
            Label = revision.Label ?? FallbackLabel,
            Origin = string.IsNullOrWhiteSpace(revision.Origin) ? NoteRevisionOrigin.User : revision.Origin,
            Content = revision.Content ?? string.Empty
        };
    }

    private static async Task<NoteRevision?> TryLoadRevisionAsync(
        string directory,
        string documentId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        if (IsSafeSegment(revisionId))
        {
            var direct = Path.Combine(directory, RevisionsDirectoryName, revisionId + ".json");
            if (File.Exists(direct))
            {
                var found = await TryReadRevisionAsync(direct, documentId, cancellationToken).ConfigureAwait(false);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        // Fall back to a scan: the id may have been recorded before the file naming, or the caller
        // may be holding an id read from an older layout.
        foreach (var file in EnumerateRevisionFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var revision = await TryReadRevisionAsync(file, documentId, cancellationToken).ConfigureAwait(false);
            if (revision is not null && string.Equals(revision.Id, revisionId, StringComparison.OrdinalIgnoreCase))
            {
                return revision;
            }
        }

        return null;
    }

    /// <summary>Opens shared so a concurrent atomic replace of the same path cannot be blocked by a reader.</summary>
    private static async Task<string?> TryReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset GetFallbackTimestamp(string directory)
    {
        try
        {
            var content = Path.Combine(directory, ContentFileName);
            return File.Exists(content)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(content), TimeSpan.Zero)
                : new DateTimeOffset(Directory.GetCreationTimeUtc(directory), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static bool MatchesMetadata(DocumentMetadata? metadata, string term)
    {
        if (metadata is null)
        {
            return false;
        }

        if (metadata.Title is { Length: > 0 } title && title.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (metadata.Project is { Length: > 0 } project && project.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return metadata.Tags is { } tags &&
               tags.Any(tag => tag is not null && tag.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // ---- directory resolution ------------------------------------------------------------------

    private IEnumerable<string> EnumerateDocumentDirectories()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(RootDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> EnumerateRevisionFiles(string directory)
    {
        var revisions = Path.Combine(directory, RevisionsDirectoryName);
        if (!Directory.Exists(revisions))
        {
            return [];
        }

        try
        {
            var files = Directory
                .GetFiles(revisions, "*.json", SearchOption.TopDirectoryOnly)
                .Where(static f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // The stamp prefix makes an ordinal name sort a chronological sort.
            Array.Sort(files, static (a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTime? TryReadNewestStamp(string documentDirectory)
    {
        var files = EnumerateRevisionFiles(documentDirectory);
        if (files.Count == 0)
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(files[^1]);
        if (stem.Length < RevisionStampLength)
        {
            return null;
        }

        return DateTime.TryParseExact(
            stem[..RevisionStampLength],
            RevisionStampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Cached like <c>FileSystemNoteRepository.GetSessionDirectory</c>: hit the cache, then probe
    /// the expected path, then fall back to a full rescan that repopulates the cache.
    /// </summary>
    private string? TryGetDocumentDirectory(string documentId)
    {
        if (_directories.TryGetValue(documentId, out var cached) && Directory.Exists(cached))
        {
            return cached;
        }

        if (IsSafeSegment(documentId))
        {
            var direct = Path.Combine(RootDirectory, documentId);
            if (Directory.Exists(direct))
            {
                _directories[documentId] = direct;
                return direct;
            }
        }

        foreach (var directory in EnumerateDocumentDirectories())
        {
            var id = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            _directories[id] = directory;

            if (string.Equals(id, documentId, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>Guards the direct-path probe against ids carrying separators or "..".</summary>
    private static bool IsSafeSegment(string value) =>
        value.Length > 0 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        value is not "." and not ".." &&
        !Path.IsPathRooted(value);

    // ---- writers -----------------------------------------------------------------------------

    private DocumentWriter GetWriter(string documentId)
    {
        if (_writers.TryGetValue(documentId, out var existing))
        {
            return existing;
        }

        lock (_writerLock)
        {
            if (_writers.TryGetValue(documentId, out existing))
            {
                return existing;
            }

            var created = new DocumentWriter();
            _writers[documentId] = created;
            return created;
        }
    }

    private async Task CloseWriterAsync(string documentId)
    {
        DocumentWriter? writer;
        lock (_writerLock)
        {
            if (!_writers.TryRemove(documentId, out writer))
            {
                return;
            }
        }

        await writer.DisposeAsync().ConfigureAwait(false);
    }

    // ---- persistence helpers -------------------------------------------------------------------

    private static Task WriteMetadataAsync(string directory, NoteDocument document, CancellationToken cancellationToken) =>
        WriteAtomicAsync(
            Path.Combine(directory, DocumentFileName),
            JsonSerializer.Serialize(
                new DocumentMetadata(
                    document.Id,
                    document.Title,
                    document.Project,
                    document.CreatedUtc,
                    document.UpdatedUtc,
                    document.Tags,
                    document.SourceSessionId),
                FileSystemNoteRepository.IndentedJsonOptions),
            cancellationToken);

    /// <summary>
    /// Deliberate duplicate of <c>FileSystemNoteRepository.WriteAtomicAsync</c>, which is private
    /// and lives in a file this store may not edit. Same contract: write a sibling temp file and
    /// move it over the target, so a crash mid-write leaves the previous good file in place.
    /// </summary>
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

    /// <summary>Deliberate duplicate of the private helper of the same name on the session repository.</summary>
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

    private static string NewShortId() => Guid.NewGuid().ToString("N")[..8];

    private static string? NormalizeProject(string? project) =>
        string.IsNullOrWhiteSpace(project) ? null : project.Trim();

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        tags is null
            ? []
            : [.. tags.Where(static t => !string.IsNullOrWhiteSpace(t)).Select(static t => t.Trim())];

    /// <summary>
    /// What actually lands in <c>document.json</c>. <see cref="NoteDocument.Content"/> is
    /// deliberately absent: the body lives in <c>content.md</c> exactly once and is rehydrated on
    /// load, so the two can never drift.
    /// </summary>
    private sealed record DocumentMetadata(
        string Id,
        string Title,
        string? Project,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        IReadOnlyList<string>? Tags,
        string? SourceSessionId);

    /// <summary>
    /// One gate per document so a keystroke-rate autosave, an AI action completing and a revert can
    /// never interleave their revision-then-content write pairs.
    /// </summary>
    private sealed class DocumentWriter : IAsyncDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public async ValueTask DisposeAsync()
        {
            // Waiting is the flush: every write completes before its holder releases the gate, so
            // once we own it there is nothing outstanding to lose.
            await Gate.WaitAsync().ConfigureAwait(false);
            Gate.Release();
            Gate.Dispose();
        }
    }
}

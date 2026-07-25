namespace NoteScribe.Core.Notes.Documents;

/// <summary>
/// A standalone note the user authors on the notes page, independent of any recording session.
/// </summary>
/// <remarks>
/// A document is the unit the AI quick actions operate on. Every change routed through
/// <see cref="INoteDocumentStore.SaveAsync"/> pushes the <em>previous</em> body onto a revision
/// stack, so an action the user dislikes is always one <see cref="INoteDocumentStore.RevertAsync"/>
/// away from being undone.
/// </remarks>
/// <param name="Id">Stable id; also the on-disk folder name under <c>&lt;NotesRoot&gt;/_documents/</c>.</param>
/// <param name="Title">User-facing title. Never blank — a blank title becomes "Untitled".</param>
/// <param name="Project">Optional grouping, e.g. the client name. Null when unfiled.</param>
/// <param name="Content">The live body, markdown, newlines normalised to <c>\n</c>.</param>
/// <param name="CreatedUtc">When the document was first created.</param>
/// <param name="UpdatedUtc">When the head content or metadata last changed.</param>
/// <param name="Tags">Freeform tags. Never null; blank entries are dropped on save.</param>
/// <param name="SourceSessionId">The recording session this was seeded from, when it was.</param>
public sealed record NoteDocument(
    string Id,
    string Title,
    string? Project,
    string Content,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<string> Tags,
    string? SourceSessionId);

/// <summary>
/// One entry on a document's revision stack: what the body looked like <em>before</em> the change
/// described by <see cref="Label"/> and <see cref="Origin"/> was applied.
/// </summary>
/// <remarks>
/// Because a revision holds the pre-change text, reverting to revision N restores exactly the state
/// the document was in before change N. The oldest revision is therefore the original content.
/// </remarks>
/// <param name="Id">Sortable id, also the revision's file name stem. Unique within the document.</param>
/// <param name="DocumentId">Owning <see cref="NoteDocument.Id"/>.</param>
/// <param name="TimestampUtc">When the change that produced this snapshot was made.</param>
/// <param name="Label">Short human-readable description of the change, e.g. "Tighten prose".</param>
/// <param name="Origin">"user", "import", or "ai:&lt;actionId&gt;" — see <see cref="NoteRevisionOrigin"/>.</param>
/// <param name="Content">The body as it stood before the change.</param>
public sealed record NoteRevision(
    string Id,
    string DocumentId,
    DateTimeOffset TimestampUtc,
    string Label,
    string Origin,
    string Content);

/// <summary>
/// The three shapes <see cref="NoteRevision.Origin"/> can take. The revision list colours
/// AI-authored changes differently, so producers should go through here rather than hand-rolling
/// the "ai:" prefix.
/// </summary>
public static class NoteRevisionOrigin
{
    /// <summary>A change the user typed or explicitly asked for (including a revert).</summary>
    public const string User = "user";

    /// <summary>Content pulled in from a session transcript or an external file.</summary>
    public const string Import = "import";

    /// <summary>Prefix for a change produced by an AI quick action.</summary>
    public const string AiPrefix = "ai:";

    /// <summary>Builds the origin string for an <c>AiAction.Id</c>.</summary>
    public static string Ai(string actionId) =>
        AiPrefix + (string.IsNullOrWhiteSpace(actionId) ? "unknown" : actionId.Trim());

    /// <summary>True when the origin denotes an AI quick action.</summary>
    public static bool IsAi(string? origin) =>
        origin is not null && origin.StartsWith(AiPrefix, StringComparison.Ordinal);

    /// <summary>The action id behind an "ai:" origin, or null when the origin is not an AI one.</summary>
    public static string? ActionId(string? origin) =>
        IsAi(origin) ? origin![AiPrefix.Length..] : null;
}

/// <summary>
/// Persists note documents together with their revision history.
/// </summary>
/// <remarks>
/// The contract that matters most: <b>the user can always get their text back</b>. Implementations
/// must never lose the previous body when applying a change, and must degrade gracefully — a torn
/// or missing metadata file may not hide a document whose body is still on disk.
/// </remarks>
public interface INoteDocumentStore
{
    /// <summary>Root of the document tree. Documents are direct children of this folder.</summary>
    string RootDirectory { get; }

    /// <summary>
    /// All documents ordered by <see cref="NoteDocument.UpdatedUtc"/> descending.
    /// </summary>
    /// <param name="search">
    /// Null or blank returns everything. Otherwise a case-insensitive match over title, project,
    /// tags and body.
    /// </param>
    Task<IReadOnlyList<NoteDocument>> ListAsync(string? search, CancellationToken cancellationToken);

    /// <summary>Loads one document including its body, or null when it does not exist.</summary>
    Task<NoteDocument?> LoadAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>Creates a new document. A blank title becomes "Untitled".</summary>
    Task<NoteDocument> CreateAsync(string title, string? project, string content,
                                   string? sourceSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="document"/> as the new head and pushes a revision capturing the
    /// PREVIOUS content, labelled with <paramref name="revisionLabel"/> and
    /// <paramref name="origin"/>. A save whose content matches the head writes no revision but
    /// still refreshes metadata and <see cref="NoteDocument.UpdatedUtc"/>.
    /// </summary>
    Task<NoteDocument> SaveAsync(NoteDocument document, string revisionLabel, string origin,
                                  CancellationToken cancellationToken);

    /// <summary>Changes the title only. The id and folder name never change.</summary>
    Task<NoteDocument> RenameAsync(string documentId, string newTitle, CancellationToken cancellationToken);

    /// <summary>Removes the document and its entire revision history. A no-op when absent.</summary>
    Task DeleteAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>Revisions oldest first. Entry zero is the original content.</summary>
    Task<IReadOnlyList<NoteRevision>> ListRevisionsAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>One revision, or null when the document or revision is gone.</summary>
    Task<NoteRevision?> LoadRevisionAsync(string documentId, string revisionId, CancellationToken cancellationToken);

    /// <summary>
    /// Restores the revision's content as the new head. The revert is itself recorded as a
    /// revision (origin <see cref="NoteRevisionOrigin.User"/>) so it can be undone in turn.
    /// </summary>
    Task<NoteDocument> RevertAsync(string documentId, string revisionId, CancellationToken cancellationToken);

    /// <summary>Absolute path to the document's own folder.</summary>
    string GetDocumentDirectory(string documentId);
}

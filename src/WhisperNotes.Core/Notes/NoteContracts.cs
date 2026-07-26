namespace WhisperNotes.Core.Notes;

/// <summary>Where a line in the notes came from.</summary>
public enum NoteEntryKind
{
    /// <summary>Produced by speech-to-text.</summary>
    Dictation,

    /// <summary>Typed by the user during the session.</summary>
    Manual,

    /// <summary>A user-dropped bookmark, e.g. "decision made here".</summary>
    Marker,

    /// <summary>Flagged as a follow-up. These get their own section in the exported markdown.</summary>
    ActionItem
}

/// <summary>One line of the running notes.</summary>
/// <param name="Id">Stable id, unique within the session.</param>
/// <param name="TimestampUtc">Wall-clock time the line was captured.</param>
/// <param name="Offset">Offset from session start — what you cite in a meeting recap.</param>
/// <param name="Kind">Origin of the line.</param>
/// <param name="Text">The content.</param>
/// <param name="Speaker">Optional attribution; null when unknown.</param>
/// <param name="Confidence">Transcriber confidence for dictation lines, else null.</param>
/// <param name="EndOffset">
/// Where the line stops. Null for a typed note, which happens at an instant rather than over a
/// span, and for dictation captured before spans were recorded — readers must treat it as unknown
/// rather than as zero.
/// </param>
public sealed record NoteEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    TimeSpan Offset,
    NoteEntryKind Kind,
    string Text,
    string? Speaker = null,
    float? Confidence = null,
    TimeSpan? EndOffset = null);

/// <summary>A single recording/dictation session — one meeting, one file.</summary>
/// <param name="Id">Stable id; also the on-disk folder name.</param>
/// <param name="Title">User-facing title, defaults to a timestamp.</param>
/// <param name="Project">Optional grouping, e.g. the client name. Becomes a folder level.</param>
/// <param name="StartedUtc">Session start.</param>
/// <param name="EndedUtc">Null while recording.</param>
/// <param name="SourceDescription">e.g. "Loopback: Speakers (Realtek)" or "video: standup.mp4".</param>
/// <param name="Tags">Freeform tags, written into the markdown front matter.</param>
/// <param name="ModelUsed">Which whisper model produced the dictation.</param>
public sealed record NoteSession(
    string Id,
    string Title,
    string? Project,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string SourceDescription,
    IReadOnlyList<string> Tags,
    string? ModelUsed)
{
    public bool IsActive => EndedUtc is null;

    public TimeSpan Duration => (EndedUtc ?? DateTimeOffset.UtcNow) - StartedUtc;
}

/// <summary>Filter for browsing past sessions in the UI.</summary>
/// <param name="Project">Restrict to one project, or null for all.</param>
/// <param name="From">Inclusive lower bound on start time.</param>
/// <param name="To">Exclusive upper bound on start time.</param>
/// <param name="TextContains">Case-insensitive match against title and entry text.</param>
public sealed record NoteQuery(
    string? Project = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? TextContains = null);

/// <summary>
/// Persists sessions to an organised directory tree. Writes must be append-only and
/// crash-safe: if the app dies mid-meeting, everything already spoken is still on disk.
/// </summary>
public interface INoteRepository
{
    /// <summary>Root of the notes tree.</summary>
    string RootDirectory { get; }

    Task<NoteSession> CreateSessionAsync(
        string title,
        string? project,
        string sourceDescription,
        IReadOnlyList<string> tags,
        string? modelUsed,
        CancellationToken cancellationToken);

    /// <summary>Appends one line. Must be safe to call at dictation rate (several per second).</summary>
    Task AppendEntryAsync(string sessionId, NoteEntry entry, CancellationToken cancellationToken);

    /// <summary>Replaces the text of an entry the user edited in the UI.</summary>
    Task UpdateEntryAsync(string sessionId, NoteEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<NoteSession>> ListSessionsAsync(NoteQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<NoteEntry>> LoadEntriesAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Rewrites <c>notes.md</c> from the transcript as it currently stands. The markdown is a
    /// derived view of the jsonl, but it is also the artefact the user actually shares, so an
    /// edit that only reaches the transcript leaves the shared copy quietly wrong. Unlike
    /// <see cref="FinalizeSessionAsync"/> this stamps no end time and closes no append handle,
    /// so it is safe to call on a session that is still recording.
    /// </summary>
    Task RerenderAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Stamps the end time and renders the human-readable markdown export.</summary>
    /// <param name="contentDuration">
    /// How long the transcribed material runs, when that differs from elapsed wall-clock time.
    /// A live session ends when the clock says so, but a recording transcribed from a file ends
    /// after however long the recording is — not after however long decoding took. Pass null for
    /// live capture; pass the media duration for file ingest.
    /// </param>
    Task<NoteSession> FinalizeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken,
        TimeSpan? contentDuration = null);

    /// <summary>Absolute path to the session's own folder.</summary>
    string GetSessionDirectory(string sessionId);
}

/// <summary>Renders a finished session to markdown.</summary>
public interface INoteExporter
{
    string Render(NoteSession session, IReadOnlyList<NoteEntry> entries);
}

using System.Globalization;
using System.Text;
using WhisperNotes.Core.Notes.Documents;

namespace WhisperNotes.App.DesignData;

/// <summary>
/// In-memory stand-in for the document store, pre-loaded with a few realistic notes that already
/// have history — including AI-authored revisions, so the revert and diff surfaces have something
/// to show in the designer.
/// </summary>
internal sealed class FakeNoteDocumentStore : INoteDocumentStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NoteDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NoteRevision>> _revisions = new(StringComparer.Ordinal);

    public FakeNoteDocumentStore()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), "WhisperNotes-SampleNotes", "_documents");

        var now = DateTimeOffset.Now;

        Seed(
            id: "northwind-cutover-a41c",
            title: "Northwind cutover — decisions",
            project: "Northwind",
            created: now.AddDays(-9),
            updated: now.AddHours(-3),
            tags: ["migration", "finance"],
            sourceSessionId: "20260224-090500-northwind-standup",
            head: """
                # Northwind cutover

                **Outcome** — Cutover holds on 14 March. Credit-note rounding is fixed afterwards and
                finance reconciles manually for the first month.

                ## Decisions
                - Cut over on 14 March (Priya).
                - Fix currency rounding post-cutover (Tom).
                - Accept manual reconciliation for month one, pending written sign-off.

                ## Open questions
                - Who approves rollback if the smoke tests fail?
                """,
            history:
            [
                ("Original transcript import", NoteRevisionOrigin.Import, now.AddDays(-9), """
                    priya right lets start with the northwind migration were two weeks from the cutover
                    and i want to know whether the reconciliation job is finished tom its finished for
                    invoices credit notes are still failing on the currency rounding about one in four
                    hundred records
                    """),
                ("Clean up transcript", NoteRevisionOrigin.Ai("cleanup-transcript"), now.AddDays(-9).AddMinutes(4), """
                    **Priya:** Right, let's start with the Northwind migration. We're two weeks from the
                    cutover and I want to know whether the reconciliation job is finished.

                    **Tom:** It's finished for invoices. Credit notes are still failing on the currency
                    rounding — about one in four hundred records.
                    """),
                ("Decision log", NoteRevisionOrigin.Ai("decision-log"), now.AddHours(-3), """
                    # Northwind cutover

                    ## Decisions
                    - Cut over on 14 March (Priya).
                    - Fix currency rounding post-cutover (Tom).
                    """),
            ]);

        Seed(
            id: "acme-discovery-7b02",
            title: "Acme discovery — requirements",
            project: "Acme",
            created: now.AddDays(-4),
            updated: now.AddDays(-1),
            tags: ["discovery", "requirements"],
            sourceSessionId: null,
            head: """
                # Acme discovery

                ### Functional
                - **REQ-01** — The system shall import supplier catalogues nightly.
                - **REQ-02** — The system shall flag price changes above 5%.

                ### Non-functional
                - **REQ-03** — Imports shall complete within 30 minutes.

                ### Needs clarification
                - Is the 5% threshold per line item or per order?
                """,
            history:
            [
                ("First draft", NoteRevisionOrigin.User, now.AddDays(-4), """
                    # Acme discovery

                    They want nightly supplier imports. Something about flagging big price jumps —
                    5% was the number mentioned. Imports currently take about 45 minutes which is
                    too slow for them.
                    """),
                ("Extract requirements", NoteRevisionOrigin.Ai("extract-requirements"), now.AddDays(-1), """
                    # Acme discovery

                    ### Functional
                    - **REQ-01** — The system shall import supplier catalogues nightly.
                    - **REQ-02** — The system shall flag price changes above 5%.
                    """),
            ]);

        Seed(
            id: "weekly-notes-1d55",
            title: "Weekly notes",
            project: null,
            created: now.AddDays(-30),
            updated: now.AddDays(-6),
            tags: [],
            sourceSessionId: null,
            head: """
                # Weekly notes

                - Chase the Acme contract redline.
                - Book the Northwind retro for the week after cutover.
                - Write up the rounding fix so it does not get rediscovered next quarter.
                """,
            history:
            [
                ("Original", NoteRevisionOrigin.User, now.AddDays(-30), "# Weekly notes\n\n- Chase the Acme contract redline."),
                ("Tighten prose", NoteRevisionOrigin.Ai("tighten-prose"), now.AddDays(-6), """
                    # Weekly notes

                    - Chase the Acme contract redline.
                    - Book the Northwind retro.
                    """),
            ]);
    }

    public string RootDirectory { get; }

    public Task<IReadOnlyList<NoteDocument>> ListAsync(string? search, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IEnumerable<NoteDocument> documents = _documents.Values;

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                documents = documents.Where(d =>
                    Contains(d.Title, term)
                    || Contains(d.Project, term)
                    || Contains(d.Content, term)
                    || d.Tags.Any(tag => Contains(tag, term)));
            }

            IReadOnlyList<NoteDocument> result = [.. documents.OrderByDescending(d => d.UpdatedUtc)];
            return Task.FromResult(result);
        }
    }

    public Task<NoteDocument?> LoadAsync(string documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_documents.GetValueOrDefault(documentId ?? string.Empty));
        }
    }

    public Task<NoteDocument> CreateAsync(
        string title,
        string? project,
        string content,
        string? sourceSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.Now;
        var clean = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        var id = $"{Slug(clean)}-{Guid.NewGuid().ToString("n")[..4]}";

        var document = new NoteDocument(
            id, clean, string.IsNullOrWhiteSpace(project) ? null : project.Trim(),
            content ?? string.Empty, now, now, [], sourceSessionId);

        lock (_gate)
        {
            _documents[id] = document;
            _revisions[id] = [];
        }

        return Task.FromResult(document);
    }

    public Task<NoteDocument> SaveAsync(
        NoteDocument document,
        string revisionLabel,
        string origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_documents.TryGetValue(document.Id, out var previous)
                && !string.Equals(previous.Content, document.Content, StringComparison.Ordinal))
            {
                // The revision captures what the text looked like *before* this save.
                Push(document.Id, revisionLabel, origin, previous.Content, DateTimeOffset.Now);
            }

            var saved = document with { UpdatedUtc = DateTimeOffset.Now };
            _documents[document.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<NoteDocument> RenameAsync(string documentId, string newTitle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var document = Require(documentId);
            var renamed = document with
            {
                Title = string.IsNullOrWhiteSpace(newTitle) ? "Untitled" : newTitle.Trim(),
                UpdatedUtc = DateTimeOffset.Now,
            };

            _documents[documentId] = renamed;
            return Task.FromResult(renamed);
        }
    }

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _documents.Remove(documentId ?? string.Empty);
            _revisions.Remove(documentId ?? string.Empty);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NoteRevision>> ListRevisionsAsync(string documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<NoteRevision> revisions = _revisions.TryGetValue(documentId ?? string.Empty, out var list)
                ? [.. list.OrderBy(r => r.Id, StringComparer.Ordinal)]
                : [];

            return Task.FromResult(revisions);
        }
    }

    public Task<NoteRevision?> LoadRevisionAsync(
        string documentId,
        string revisionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var revision = _revisions.TryGetValue(documentId ?? string.Empty, out var list)
                ? list.FirstOrDefault(r => string.Equals(r.Id, revisionId, StringComparison.Ordinal))
                : null;

            return Task.FromResult(revision);
        }
    }

    public Task<NoteDocument> RevertAsync(string documentId, string revisionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var document = Require(documentId);
            var revision = _revisions.TryGetValue(documentId, out var list)
                ? list.FirstOrDefault(r => string.Equals(r.Id, revisionId, StringComparison.Ordinal))
                : null;

            if (revision is null)
            {
                return Task.FromResult(document);
            }

            // The revert is itself undoable: push the current head before overwriting it.
            Push(documentId, $"Before revert to \"{revision.Label}\"", NoteRevisionOrigin.User,
                document.Content, DateTimeOffset.Now);

            var reverted = document with { Content = revision.Content, UpdatedUtc = DateTimeOffset.Now };
            _documents[documentId] = reverted;
            return Task.FromResult(reverted);
        }
    }

    public string GetDocumentDirectory(string documentId) =>
        Path.Combine(RootDirectory, string.IsNullOrWhiteSpace(documentId) ? "unknown" : documentId);

    private NoteDocument Require(string documentId) =>
        _documents.TryGetValue(documentId ?? string.Empty, out var document)
            ? document
            : throw new InvalidOperationException($"No sample document with id '{documentId}'.");

    private void Push(string documentId, string label, string origin, string content, DateTimeOffset timestamp)
    {
        if (!_revisions.TryGetValue(documentId, out var list))
        {
            list = [];
            _revisions[documentId] = list;
        }

        var id = timestamp.UtcDateTime.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)
                 + "-" + Guid.NewGuid().ToString("n")[..6];

        list.Add(new NoteRevision(
            id,
            documentId,
            timestamp,
            string.IsNullOrWhiteSpace(label) ? "Edit" : label.Trim(),
            string.IsNullOrWhiteSpace(origin) ? NoteRevisionOrigin.User : origin,
            content));
    }

    private void Seed(
        string id,
        string title,
        string? project,
        DateTimeOffset created,
        DateTimeOffset updated,
        string[] tags,
        string? sourceSessionId,
        string head,
        (string Label, string Origin, DateTimeOffset At, string Content)[] history)
    {
        _documents[id] = new NoteDocument(id, title, project, head, created, updated, tags, sourceSessionId);
        _revisions[id] = [];

        foreach (var (label, origin, at, content) in history)
        {
            Push(id, label, origin, content, at);
        }
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "note" : slug[..Math.Min(slug.Length, 40)];
    }
}

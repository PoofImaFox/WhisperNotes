using System.Globalization;
using System.Text;

namespace WhisperNotes.Core.Notes;

/// <summary>
/// Renders a session as markdown with YAML front matter: action items first because that is the
/// part you act on, then the transcript as merged prose rather than a line-per-utterance log.
/// </summary>
public sealed class MarkdownNoteExporter : INoteExporter
{
    /// <summary>
    /// A silence longer than this reads as a break rather than a breath, so it ends the paragraph
    /// and the next one gets its own timestamp.
    /// </summary>
    private static readonly TimeSpan ParagraphGap = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Ceiling on a merged run, regardless of pauses. Someone who holds the floor for ten minutes
    /// still has to be navigable: without this the transcript offers no timestamp to scrub to.
    /// </summary>
    private static readonly TimeSpan ParagraphSpan = TimeSpan.FromSeconds(45);

    public string Render(NoteSession session, IReadOnlyList<NoteEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(session);
        entries ??= [];

        var builder = new StringBuilder(1024);
        WriteFrontMatter(builder, session);

        builder.Append("# ").AppendLine(Inline(session.Title));
        builder.AppendLine();

        WriteActionItems(builder, entries);
        WriteTranscript(builder, entries);

        return builder.ToString();
    }

    private static void WriteFrontMatter(StringBuilder builder, NoteSession session)
    {
        builder.AppendLine("---");
        builder.Append("title: ").AppendLine(Yaml.Scalar(session.Title));
        builder.Append("project: ").AppendLine(Yaml.Scalar(session.Project ?? FileSystemNoteRepository.UnfiledProject));
        builder.Append("date: ").AppendLine(Yaml.Scalar(
            session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)));
        builder.Append("duration: ").AppendLine(Yaml.Scalar(FormatDuration(session.Duration)));
        builder.Append("source: ").AppendLine(Yaml.Scalar(session.SourceDescription));
        builder.Append("model: ").AppendLine(Yaml.Scalar(session.ModelUsed ?? "unknown"));

        var tags = session.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        builder.Append("tags: [")
            .Append(string.Join(", ", tags.Select(Yaml.Scalar)))
            .AppendLine("]");

        builder.AppendLine("---");
        builder.AppendLine();
    }

    private static void WriteActionItems(StringBuilder builder, IReadOnlyList<NoteEntry> entries)
    {
        var actionItems = entries.Where(e => e.Kind == NoteEntryKind.ActionItem).ToArray();
        if (actionItems.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Action items");
        builder.AppendLine();

        foreach (var item in actionItems)
        {
            builder.Append("- [ ] ").Append(Inline(item.Text));
            if (!string.IsNullOrWhiteSpace(item.Speaker))
            {
                builder.Append(" — ").Append(Inline(item.Speaker));
            }

            builder.Append(" (").Append(FormatOffset(item.Offset)).AppendLine(")");
        }

        builder.AppendLine();
    }

    private static void WriteTranscript(StringBuilder builder, IReadOnlyList<NoteEntry> entries)
    {
        builder.AppendLine("## Transcript");
        builder.AppendLine();

        if (entries.Count == 0)
        {
            builder.AppendLine("_No entries were captured._");
            return;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            switch (entry.Kind)
            {
                case NoteEntryKind.Dictation:
                    // Consecutive dictation from one speaker is one utterance split by the chunker;
                    // stitch it back into a paragraph so the export reads as prose. The run has to
                    // be bounded, though: an un-attributed session is one long stretch of entries
                    // whose speakers all compare equal, and merging on that alone collapses an hour
                    // of transcript into a single block under a single "00:00:00".
                    var text = new StringBuilder(Inline(entry.Text));
                    var spoken = EndOf(entry);

                    while (i + 1 < entries.Count && Continues(entries[i + 1], entry, spoken))
                    {
                        i++;
                        spoken = EndOf(entries[i]);

                        var continuation = Inline(entries[i].Text);
                        if (continuation.Length == 0)
                        {
                            continue;
                        }

                        if (text.Length > 0)
                        {
                            text.Append(' ');
                        }

                        text.Append(continuation);
                    }

                    builder.Append("**[").Append(FormatOffset(entry.Offset)).Append("]** ");
                    if (!string.IsNullOrWhiteSpace(entry.Speaker))
                    {
                        builder.Append("**").Append(Inline(entry.Speaker)).Append(":** ");
                    }

                    builder.AppendLine(text.ToString());
                    builder.AppendLine();
                    break;

                case NoteEntryKind.Marker:
                    builder.Append("> **MARKER [").Append(FormatOffset(entry.Offset)).Append("]** ")
                        .AppendLine(Inline(entry.Text));
                    builder.AppendLine();
                    break;

                case NoteEntryKind.ActionItem:
                    builder.Append("**[").Append(FormatOffset(entry.Offset)).Append("]** **TODO:** ")
                        .AppendLine(Inline(entry.Text));
                    builder.AppendLine();
                    break;

                case NoteEntryKind.Manual:
                default:
                    builder.Append("**[").Append(FormatOffset(entry.Offset)).Append("]** *(note)* ")
                        .AppendLine(Inline(entry.Text));
                    builder.AppendLine();
                    break;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="next"/> belongs to the paragraph that opened at
    /// <paramref name="start"/> and has been spoken through <paramref name="spoken"/>.
    /// </summary>
    private static bool Continues(NoteEntry next, NoteEntry start, TimeSpan? spoken) =>
        next.Kind == NoteEntryKind.Dictation
        && string.Equals(next.Speaker, start.Speaker, StringComparison.Ordinal)
        && next.Offset - start.Offset <= ParagraphSpan
        // A pause can only be measured against a known end. Sessions recorded before spans were
        // kept fall back to the span ceiling alone: still paragraphed, just not on the pauses.
        && (spoken is not { } end || next.Offset - end <= ParagraphGap);

    /// <summary>
    /// When an entry stops, or null if it never recorded that. Treating an unknown end as the start
    /// would make the whole of the next utterance look like a pause and break every paragraph.
    /// </summary>
    private static TimeSpan? EndOf(NoteEntry entry) =>
        entry.EndOffset is { } end && end > entry.Offset ? end : null;

    internal static string FormatOffset(TimeSpan offset)
    {
        if (offset < TimeSpan.Zero)
        {
            offset = TimeSpan.Zero;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}",
            (int)offset.TotalHours, offset.Minutes, offset.Seconds);
    }

    private static string FormatDuration(TimeSpan duration) => FormatOffset(duration);

    /// <summary>
    /// Flattens a captured line to a single markdown-safe run of inline text: newlines would break
    /// out of list items and blockquotes, and unescaped markup would restructure the document.
    /// </summary>
    internal static string Inline(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 8);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~' or '#')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

}

using System.Globalization;
using System.Text;

namespace NoteScribe.Core.Notes;

/// <summary>
/// Renders a session as markdown with YAML front matter: action items first because that is the
/// part you act on, then the transcript as merged prose rather than a line-per-utterance log.
/// </summary>
public sealed class MarkdownNoteExporter : INoteExporter
{
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
        builder.Append("title: ").AppendLine(YamlScalar(session.Title));
        builder.Append("project: ").AppendLine(YamlScalar(session.Project ?? FileSystemNoteRepository.UnfiledProject));
        builder.Append("date: ").AppendLine(YamlScalar(
            session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)));
        builder.Append("duration: ").AppendLine(YamlScalar(FormatDuration(session.Duration)));
        builder.Append("source: ").AppendLine(YamlScalar(session.SourceDescription));
        builder.Append("model: ").AppendLine(YamlScalar(session.ModelUsed ?? "unknown"));

        var tags = session.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        builder.Append("tags: [")
            .Append(string.Join(", ", tags.Select(YamlScalar)))
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
                    // stitch it back into a paragraph so the export reads as prose.
                    var text = new StringBuilder(Inline(entry.Text));
                    while (i + 1 < entries.Count &&
                           entries[i + 1].Kind == NoteEntryKind.Dictation &&
                           string.Equals(entries[i + 1].Speaker, entry.Speaker, StringComparison.Ordinal))
                    {
                        i++;
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

    /// <summary>Always emits a double-quoted YAML scalar so titles containing ':', '#' or '---' stay inert.</summary>
    internal static string YamlScalar(string? value)
    {
        var builder = new StringBuilder((value?.Length ?? 0) + 2);
        builder.Append('"');

        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}

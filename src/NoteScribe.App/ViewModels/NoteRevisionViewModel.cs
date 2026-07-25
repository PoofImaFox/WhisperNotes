using System.Globalization;
using NoteScribe.Core.Ai;
using NoteScribe.Core.Notes.Documents;

namespace NoteScribe.App.ViewModels;

/// <summary>
/// One entry in the history panel. A revision holds the body as it stood <em>before</em> the change
/// named by <see cref="Label"/>, so the <c>+N −M</c> badge is measured from this snapshot to
/// whatever replaced it.
/// </summary>
public sealed class NoteRevisionViewModel
{
    public NoteRevisionViewModel(NoteRevision revision, string successorContent, bool isOriginal)
    {
        ArgumentNullException.ThrowIfNull(revision);

        Revision = revision;
        IsOriginal = isOriginal;

        var (added, removed) = TextDiff.Stat(revision.Content, successorContent);
        Added = added;
        Removed = removed;
    }

    public NoteRevision Revision { get; }

    public string Id => Revision.Id;

    public string Label => string.IsNullOrWhiteSpace(Revision.Label) ? "Edit" : Revision.Label;

    public string TimestampText => RelativeTime.Absolute(Revision.TimestampUtc);

    public string RelativeText => RelativeTime.Describe(Revision.TimestampUtc);

    public bool IsAi => NoteRevisionOrigin.IsAi(Revision.Origin);

    public bool IsOriginal { get; }

    public int Added { get; }

    public int Removed { get; }

    /// <summary>Short badge: the action's display name for AI changes, else the origin itself.</summary>
    public string OriginText
    {
        get
        {
            if (!IsAi)
            {
                return string.Equals(Revision.Origin, NoteRevisionOrigin.Import, StringComparison.Ordinal)
                    ? "IMPORT"
                    : "YOU";
            }

            var actionId = NoteRevisionOrigin.ActionId(Revision.Origin);
            var action = actionId is null ? null : AiActionCatalog.ById(actionId);
            return (action?.Name ?? actionId ?? "AI").ToUpperInvariant();
        }
    }

    public string StatText => string.Create(CultureInfo.InvariantCulture, $"+{Added} −{Removed}");

    public bool HasStat => Added > 0 || Removed > 0;

    public string TooltipText => string.Create(
        CultureInfo.CurrentCulture,
        $"{Label}\n{TimestampText}\n{StatText} against the version that replaced it");
}

/// <summary>
/// One rendered line of a unified diff, or the "…" marker standing in for the lines
/// <see cref="TextDiff.Unified"/> elided between two hunks.
/// </summary>
public sealed class DiffLineViewModel
{
    private DiffLineViewModel()
    {
        IsSeparator = true;
        Marker = string.Empty;
        Text = "⋯";
        LineNumberText = string.Empty;
    }

    public DiffLineViewModel(DiffLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        IsAdded = line.Kind == DiffKind.Added;
        IsRemoved = line.Kind == DiffKind.Removed;
        IsContext = line.Kind == DiffKind.Unchanged;

        Marker = line.Kind switch
        {
            DiffKind.Added => "+",
            DiffKind.Removed => "−",
            _ => " "
        };

        // An empty line still needs to paint its full-width background strip.
        Text = line.Text.Length == 0 ? " " : line.Text;

        var number = line.Kind == DiffKind.Added ? line.RightLine : line.LeftLine;
        LineNumberText = number is null
            ? string.Empty
            : number.Value.ToString(CultureInfo.InvariantCulture);
    }

    public static DiffLineViewModel Elision() => new();

    public string Marker { get; }

    public string Text { get; }

    public string LineNumberText { get; }

    public bool IsAdded { get; }

    public bool IsRemoved { get; }

    public bool IsContext { get; }

    public bool IsSeparator { get; }
}

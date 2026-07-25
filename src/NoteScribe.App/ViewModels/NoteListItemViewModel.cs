using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NoteScribe.Core.Notes;
using NoteScribe.Core.Notes.Documents;

namespace NoteScribe.App.ViewModels;

/// <summary>One row in the note library list.</summary>
/// <remarks>
/// Holds the whole <see cref="NoteDocument"/>, body included, because
/// <see cref="INoteDocumentStore.ListAsync"/> already pays for the read and handing a partial
/// document to <see cref="INoteDocumentStore.SaveAsync"/> would be data loss.
/// </remarks>
public sealed partial class NoteListItemViewModel : ObservableObject
{
    public NoteListItemViewModel(NoteDocument document)
    {
        Document = document;
    }

    public NoteDocument Document { get; private set; }

    public string Id => Document.Id;

    public string Title => string.IsNullOrWhiteSpace(Document.Title) ? "Untitled" : Document.Title;

    public string ProjectText => Document.Project ?? string.Empty;

    public bool HasProject => !string.IsNullOrWhiteSpace(Document.Project);

    public string UpdatedText => RelativeTime.Describe(Document.UpdatedUtc);

    /// <summary>First line of body text that is not a heading — enough to recognise the note by.</summary>
    public string PreviewText => BuildPreview(Document.Content);

    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>True when the newest revision was written by an AI quick action.</summary>
    [ObservableProperty]
    public partial bool IsAiTouched { get; set; }

    public void Update(NoteDocument document)
    {
        Document = document;

        // Everything visible is a projection of the record, so one blanket notification is both
        // correct and cheaper than eight targeted ones.
        OnPropertyChanged(string.Empty);
    }

    private static string BuildPreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line is "---" or "***" or "___")
            {
                continue;
            }

            return line.Length > 120 ? line[..120] : line;
        }

        return string.Empty;
    }
}

/// <summary>A finished recording session offered as the seed for a new note.</summary>
public sealed class MeetingPickViewModel
{
    public MeetingPickViewModel(NoteSession session)
    {
        Session = session;
    }

    public NoteSession Session { get; }

    public string Id => Session.Id;

    public string Title => string.IsNullOrWhiteSpace(Session.Title) ? "Untitled meeting" : Session.Title;

    public string ProjectText => Session.Project ?? "No project";

    public string WhenText => Session.StartedUtc.ToLocalTime()
        .ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);

    public string DurationText
    {
        get
        {
            var duration = Session.Duration;
            return duration.TotalHours >= 1
                ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m")
                : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (int)duration.TotalMinutes)}m");
        }
    }
}

/// <summary>"3m ago" style stamps — the only time format that reads well in a dense list.</summary>
internal static class RelativeTime
{
    public static string Describe(DateTimeOffset timestamp)
    {
        var now = DateTimeOffset.Now;
        var local = timestamp.ToLocalTime();
        var delta = now - local;

        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 45)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)delta.TotalMinutes}m ago");
        }

        if (delta.TotalHours < 24 && local.Date == now.Date)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)delta.TotalHours}h ago");
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"yesterday {local:HH:mm}");
        }

        return local.Year == now.Year
            ? local.ToString("d MMM", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    public static string Absolute(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}

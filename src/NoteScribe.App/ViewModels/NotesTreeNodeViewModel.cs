using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.Core.Notes;

namespace NoteScribe.App.ViewModels;

public abstract partial class NotesTreeNodeViewModel : ObservableObject
{
    public ObservableCollection<NotesTreeNodeViewModel> Children { get; } = [];

    /// <summary>What a screen reader announces for this row — the templates are pure layout.</summary>
    public abstract string DisplayName { get; }

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
}

/// <summary>Top level of the browser: the project folder, which is also a folder on disk.</summary>
public sealed class ProjectNodeViewModel(string name, int sessionCount) : NotesTreeNodeViewModel
{
    public string Name { get; } = name;

    public string CountText { get; } = sessionCount == 1 ? "1 session" : $"{sessionCount} sessions";

    public override string DisplayName => Name;
}

/// <summary>Second level: one calendar day.</summary>
public sealed class DateNodeViewModel(DateOnly date, int sessionCount) : NotesTreeNodeViewModel
{
    public string Label { get; } = Describe(date);

    public string CountText { get; } = sessionCount == 1 ? "1" : sessionCount.ToString(CultureInfo.CurrentCulture);

    public override string DisplayName => Label;

    private static string Describe(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var delta = today.DayNumber - date.DayNumber;

        return delta switch
        {
            0 => $"Today · {date:ddd d MMM}",
            1 => $"Yesterday · {date:ddd d MMM}",
            _ => date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)
        };
    }
}

/// <summary>Leaf: an actual session the user can open.</summary>
public sealed partial class SessionNodeViewModel(NoteSession session, Action<NoteSession> openFolder)
    : NotesTreeNodeViewModel
{
    public NoteSession Session { get; } = session;

    public string Title { get; } = session.Title;

    public string TimeText { get; } = session.StartedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    public string DateText { get; } =
        session.StartedUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);

    public string DurationText { get; } = Format(session.Duration);

    public bool IsFromVideo { get; } =
        session.SourceDescription.StartsWith("video:", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] public partial string EntryCountText { get; set; } = "…";

    public override string DisplayName => $"{Title}, {TimeText}, {DurationText}";

    [RelayCommand]
    private void OpenFolder() => openFolder(Session);

    private static string Format(TimeSpan duration) => duration.TotalHours >= 1
        ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalHours}h {duration.Minutes:00}m")
        : string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalMinutes}m");
}

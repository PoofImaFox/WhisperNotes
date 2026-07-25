using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.Core.Notes;

namespace NoteScribe.App.ViewModels;

/// <summary>
/// The notes currently on screen — either the session being recorded, or a past session opened
/// read-only from the browser.
/// </summary>
public sealed partial class SessionDocumentViewModel : ObservableObject
{
    private readonly INoteRepository _notes;
    private readonly Action<string, string, NotificationSeverity> _notify;

    public SessionDocumentViewModel(INoteRepository notes, Action<string, string, NotificationSeverity> notify)
    {
        _notes = notes;
        _notify = notify;
    }

    /// <summary>Raised after an entry is appended so the view can follow the tail.</summary>
    public event EventHandler? EntryAppended;

    /// <summary>Raised when the user explicitly asks to jump back to the live tail.</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>
    /// Raised when the log should be shown from the beginning — a finished meeting is read from
    /// the top, and asking for the tail of a batch-loaded transcript only ever lands half way.
    /// </summary>
    public event EventHandler? ScrollToTopRequested;

    public ObservableCollection<NoteEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial NoteSession? Session { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose))]
    [NotifyPropertyChangedFor(nameof(ShowJumpToLive))]
    [NotifyCanExecuteChangedFor(nameof(AddNoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddActionItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(DropMarkerCommand))]
    public partial bool IsLive { get; set; }

    [ObservableProperty] public partial string ComposerText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInterim))]
    public partial string? InterimText { get; set; }

    /// <summary>False once the user scrolls away from the tail; restored by "jump to live".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJumpToLive))]
    public partial bool AutoScroll { get; set; } = true;

    /// <summary>Only a live session has a tail worth jumping back to.</summary>
    public bool ShowJumpToLive => IsLive && !AutoScroll;

    public bool HasSession => Session is not null;

    public bool HasInterim => !string.IsNullOrEmpty(InterimText);

    public bool CanCompose => IsLive && Session is not null;

    public bool IsEmpty => Entries.Count == 0;

    public string Title => Session?.Title ?? "No session open";

    public string Subtitle => Session switch
    {
        // A live session's Duration is "now minus start" and would be stale the moment it renders;
        // the elapsed clock in the capture bar is the live readout.
        { EndedUtc: null } live => string.Create(
            CultureInfo.CurrentCulture,
            $"{live.Project ?? "_unfiled"} · started {live.StartedUtc.ToLocalTime():ddd d MMM yyyy HH:mm} · {live.SourceDescription}"),
        { } done => string.Create(
            CultureInfo.CurrentCulture,
            $"{done.Project ?? "_unfiled"} · {done.StartedUtc.ToLocalTime():ddd d MMM yyyy HH:mm} · {done.Duration:hh\\:mm\\:ss} · {done.SourceDescription}"),
        null => "Pick a session on the left, or choose a channel and press Start."
    };

    public string EntryCountText => Entries.Count == 1 ? "1 entry" : $"{Entries.Count} entries";

    public void BeginLiveSession(NoteSession session)
    {
        Session = session;
        IsLive = true;
        AutoScroll = true;
        InterimText = null;
        Entries.Clear();
        RaiseCountsChanged();
    }

    public void ShowReadOnlySession(NoteSession session, IReadOnlyList<NoteEntry> entries)
    {
        Session = session;
        IsLive = false;
        InterimText = null;
        Entries.Clear();

        foreach (var entry in entries)
        {
            Entries.Add(Wrap(entry, session.Id, canEdit: true));
        }

        RaiseCountsChanged();

        // Not a tail to follow: IsLive is false, so ShowJumpToLive stays false and nothing will
        // try to re-pin this document to the bottom.
        AutoScroll = false;
        ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called on the UI thread once an entry is safely on disk.</summary>
    public void Append(NoteEntry entry)
    {
        if (Session is not { } session)
        {
            return;
        }

        // Decoding lags the clock, so a typed note can carry a later offset than a segment that
        // arrives after it. Insert by offset to keep the record chronological rather than arrival-ordered.
        var index = Entries.Count;
        while (index > 0 && Entries[index - 1].Entry.Offset > entry.Offset)
        {
            index--;
        }

        Entries.Insert(index, Wrap(entry, session.Id, canEdit: true));
        RaiseCountsChanged();
        EntryAppended?.Invoke(this, EventArgs.Empty);
    }

    public void MarkFinalized(NoteSession finalized)
    {
        Session = finalized;
        IsLive = false;
        InterimText = null;
    }

    [RelayCommand(CanExecute = nameof(CanCompose))]
    private Task AddNoteAsync() => AddComposedAsync(NoteEntryKind.Manual);

    [RelayCommand(CanExecute = nameof(CanCompose))]
    private Task AddActionItemAsync() => AddComposedAsync(NoteEntryKind.ActionItem);

    [RelayCommand(CanExecute = nameof(CanCompose))]
    private Task DropMarkerAsync() => AddComposedAsync(NoteEntryKind.Marker, allowEmpty: true);

    [RelayCommand]
    private void JumpToLive()
    {
        AutoScroll = true;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddComposedAsync(NoteEntryKind kind, bool allowEmpty = false)
    {
        if (Session is not { } session)
        {
            return;
        }

        var text = ComposerText.Trim();
        if (text.Length == 0)
        {
            if (!allowEmpty)
            {
                return;
            }

            text = string.Create(CultureInfo.CurrentCulture, $"Marker at {DateTimeOffset.Now:HH:mm:ss}");
        }

        var now = DateTimeOffset.Now;
        var entry = new NoteEntry(
            Guid.NewGuid().ToString("n"),
            now,
            now - session.StartedUtc,
            kind,
            text);

        try
        {
            await _notes.AppendEntryAsync(session.Id, entry, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notify("Could not save that note", ex.Message, NotificationSeverity.Error);
            return;
        }

        ComposerText = "";
        AutoScroll = true;
        Append(entry);
    }

    private NoteEntryViewModel Wrap(NoteEntry entry, string sessionId, bool canEdit) =>
        new(entry, sessionId, _notes, canEdit, _notify);

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(EntryCountText));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

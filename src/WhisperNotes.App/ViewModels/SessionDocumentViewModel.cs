using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.App.ViewModels;

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

    /// <summary>
    /// The distinct speakers diarization found, in the order they first spoke. The model can tell
    /// the voices apart but never learns their names, so this is what the rename picker offers.
    /// </summary>
    public ObservableCollection<string> Speakers { get; } = [];

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginSpeakerRenameCommand))]
    public partial string? SelectedSpeaker { get; set; }

    [ObservableProperty] public partial bool IsRenamingSpeaker { get; set; }

    [ObservableProperty] public partial string SpeakerRenameText { get; set; } = "";

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

    public bool HasSpeakers => Speakers.Count > 0;

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
        IsRenamingSpeaker = false;
        Entries.Clear();
        RaiseCountsChanged();
        RefreshSpeakers();
    }

    public void ShowReadOnlySession(NoteSession session, IReadOnlyList<NoteEntry> entries)
    {
        Session = session;
        IsLive = false;
        InterimText = null;
        IsRenamingSpeaker = false;
        Entries.Clear();

        foreach (var entry in entries)
        {
            Entries.Add(Wrap(entry, session.Id, canEdit: true));
        }

        RaiseCountsChanged();
        RefreshSpeakers();

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
        RefreshSpeakers();
        EntryAppended?.Invoke(this, EventArgs.Empty);
    }

    public void MarkFinalized(NoteSession finalized)
    {
        Session = finalized;
        IsLive = false;
        InterimText = null;
    }

    /// <summary>
    /// Refreshes the on-screen rows after end-of-session diarization has already persisted the
    /// labels. The mapping is by entry id because typed notes may sit between dictated lines.
    /// </summary>
    public void ApplySpeakerLabels(IReadOnlyDictionary<string, string> labels)
    {
        foreach (NoteEntryViewModel entry in Entries)
        {
            if (labels.TryGetValue(entry.Entry.Id, out string? speaker))
            {
                entry.WithSpeaker(speaker);
            }
        }

        RefreshSpeakers();
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

    // ---- speaker rename ---------------------------------------------------------------------------

    private bool HasSpeakerSelection => SelectedSpeaker is { Length: > 0 };

    // An open rename box belongs to the speaker it was opened for; moving the picker abandons it
    // rather than silently retargeting the pending name at someone else.
    partial void OnSelectedSpeakerChanged(string? value) => IsRenamingSpeaker = false;

    [RelayCommand(CanExecute = nameof(HasSpeakerSelection))]
    private void BeginSpeakerRename()
    {
        if (SelectedSpeaker is not { Length: > 0 } speaker)
        {
            return;
        }

        SpeakerRenameText = speaker;
        IsRenamingSpeaker = true;
    }

    [RelayCommand]
    private void CancelSpeakerRename() => IsRenamingSpeaker = false;

    /// <summary>
    /// Puts a real name on a diarization cluster, across every line it owns. Whoever "Speaker 2"
    /// is, they are that person for the whole meeting, so this is a session-wide operation and
    /// belongs here rather than on the row the user happened to be looking at.
    /// </summary>
    [RelayCommand]
    private async Task CommitSpeakerRenameAsync()
    {
        IsRenamingSpeaker = false;

        if (Session is not { } session || SelectedSpeaker is not { Length: > 0 } previous)
        {
            return;
        }

        var name = SpeakerRenameText.Trim();

        // Blank or unchanged is not a correction. Renaming onto a name already in the session is,
        // though — two clusters that were one person all along merge into that person.
        if (name.Length == 0 || string.Equals(name, previous, StringComparison.Ordinal))
        {
            return;
        }

        var affected = Entries
            .Where(e => string.Equals(e.Speaker, previous, StringComparison.Ordinal))
            .ToList();

        var applied = 0;

        try
        {
            // Optimistic, one row at a time: each label flips as its own line lands, so a failure
            // part way through leaves the screen agreeing with the transcript rather than ahead of it.
            for (; applied < affected.Count; applied++)
            {
                await _notes
                    .UpdateEntryAsync(session.Id, affected[applied].WithSpeaker(name), CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // The row whose write threw is the only one now carrying a name with nothing behind it.
            affected[applied].WithSpeaker(previous);
            _notify("Could not rename that speaker", ex.Message, NotificationSeverity.Error);
        }

        if (applied > 0)
        {
            try
            {
                // Once for the whole rename: notes.md is rebuilt from the transcript every time, so
                // doing it per entry would just write the same file N times.
                await _notes.RerenderAsync(session.Id, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _notify("Renamed, but notes.md is out of date", ex.Message, NotificationSeverity.Warning);
            }
        }

        RefreshSpeakers(preferred: name);
    }

    /// <summary>
    /// Rebuilds the speaker picker from the entries on screen — diarization coins its labels as it
    /// goes, so the cast is only ever knowable from the transcript itself.
    /// </summary>
    private void RefreshSpeakers(string? preferred = null)
    {
        var distinct = new List<string>();
        foreach (var entry in Entries)
        {
            if (entry.Speaker is { Length: > 0 } speaker && !distinct.Contains(speaker, StringComparer.Ordinal))
            {
                distinct.Add(speaker);
            }
        }

        // Appends arrive at dictation rate and hardly ever change the cast; refilling the
        // collection regardless would reset the picker under the user between two words.
        if (distinct.SequenceEqual(Speakers, StringComparer.Ordinal))
        {
            if (preferred is { Length: > 0 } unchanged && Speakers.Contains(unchanged, StringComparer.Ordinal))
            {
                SelectedSpeaker = unchanged;
            }

            return;
        }

        var keep = preferred ?? SelectedSpeaker;

        Speakers.Clear();
        foreach (var speaker in distinct)
        {
            Speakers.Add(speaker);
        }

        SelectedSpeaker = keep is { Length: > 0 } wanted && Speakers.Contains(wanted, StringComparer.Ordinal)
            ? wanted
            : Speakers.FirstOrDefault();

        OnPropertyChanged(nameof(HasSpeakers));
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

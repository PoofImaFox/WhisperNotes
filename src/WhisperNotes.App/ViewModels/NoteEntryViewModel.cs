using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.App.ViewModels;

/// <summary>
/// One line of the running notes. Editable in place: whisper mishears client and product names
/// constantly and these notes are the billable record, so corrections must persist.
/// </summary>
public sealed partial class NoteEntryViewModel : ObservableObject
{
    private readonly INoteRepository _notes;
    private readonly string _sessionId;
    private readonly Action<string, string, NotificationSeverity> _notify;
    private NoteEntry _entry;

    public NoteEntryViewModel(
        NoteEntry entry,
        string sessionId,
        INoteRepository notes,
        bool canEdit,
        Action<string, string, NotificationSeverity> notify)
    {
        _entry = entry;
        _sessionId = sessionId;
        _notes = notes;
        _notify = notify;
        CanEdit = canEdit;
        Text = entry.Text;
        Draft = entry.Text;
    }

    public NoteEntry Entry => _entry;

    public bool CanEdit { get; }

    [ObservableProperty] public partial string Text { get; set; }

    /// <summary>Working copy while the row is in edit mode, so Escape can abandon it.</summary>
    [ObservableProperty] public partial string Draft { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    public partial bool IsEditing { get; set; }

    public bool IsNotEditing => !IsEditing;

    public bool IsDictation => _entry.Kind == NoteEntryKind.Dictation;

    public bool IsManual => _entry.Kind == NoteEntryKind.Manual;

    public bool IsMarker => _entry.Kind == NoteEntryKind.Marker;

    public bool IsActionItem => _entry.Kind == NoteEntryKind.ActionItem;

    public bool HasKindLabel => !IsDictation;

    public string KindLabel => _entry.Kind switch
    {
        NoteEntryKind.Manual => "NOTE",
        NoteEntryKind.Marker => "MARKER",
        NoteEntryKind.ActionItem => "ACTION",
        _ => "DICTATION"
    };

    public string OffsetText => _entry.Offset.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    public string TimeOfDayText => _entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string? Speaker => _entry.Speaker;

    public bool HasSpeaker => !string.IsNullOrWhiteSpace(_entry.Speaker);

    /// <summary>Below this the transcriber was guessing; worth a human eye before invoicing on it.</summary>
    public bool IsLowConfidence => _entry.Confidence is > 0 and < 0.55f;

    public string ConfidenceText => _entry.Confidence is { } c and > 0
        ? string.Create(CultureInfo.CurrentCulture, $"{c:P0} confidence")
        : string.Empty;

    public bool HasConfidence => _entry.Confidence is > 0;

    /// <summary>
    /// Relabels this line as part of a session-wide speaker rename, returning the entry the caller
    /// must persist. Only <see cref="SessionDocumentViewModel"/> calls this: a single row has no
    /// business relabelling its siblings, and the rename is only ever meaningful across all of them.
    /// </summary>
    public NoteEntry WithSpeaker(string? speaker)
    {
        _entry = _entry with { Speaker = speaker };

        // Speaker and HasSpeaker read straight through to _entry rather than being generated
        // observable properties, so nothing else will ever tell the chip to redraw.
        OnPropertyChanged(nameof(Speaker));
        OnPropertyChanged(nameof(HasSpeaker));

        return _entry;
    }

    /// <summary>Adopts both the display label and the durable acoustic profile behind it.</summary>
    public NoteEntry WithSpeaker(string? speaker, string? speakerProfileId)
    {
        _entry = _entry with
        {
            Speaker = speaker,
            SpeakerProfileId = speakerProfileId,
        };

        OnPropertyChanged(nameof(Speaker));
        OnPropertyChanged(nameof(HasSpeaker));
        return _entry;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void BeginEdit()
    {
        Draft = Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Draft = Text;
        IsEditing = false;
    }

    [RelayCommand]
    private async Task CommitEditAsync()
    {
        var trimmed = Draft.Trim();
        IsEditing = false;

        if (trimmed.Length == 0 || string.Equals(trimmed, Text, StringComparison.Ordinal))
        {
            Draft = Text;
            return;
        }

        var previous = _entry;
        var updated = _entry with { Text = trimmed };

        // Optimistic: show the correction straight away, roll back if the write fails.
        _entry = updated;
        Text = trimmed;

        try
        {
            await _notes.UpdateEntryAsync(_sessionId, updated, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _entry = previous;
            Text = previous.Text;
            Draft = previous.Text;
            _notify("Could not save your edit", ex.Message, NotificationSeverity.Error);
            return;
        }

        try
        {
            await _notes.RerenderAsync(_sessionId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The correction is already on disk; only the derived export missed it. Rolling the
            // edit back over that would lose good data to fix a file we can rebuild any time.
            _notify("Saved, but notes.md is out of date", ex.Message, NotificationSeverity.Warning);
        }
    }
}

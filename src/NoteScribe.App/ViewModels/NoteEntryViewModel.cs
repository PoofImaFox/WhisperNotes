using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.Core.Notes;

namespace NoteScribe.App.ViewModels;

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
        }
    }
}

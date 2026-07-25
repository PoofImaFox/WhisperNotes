using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.App.Composition;
using NoteScribe.App.Services;
using NoteScribe.Core.Ai;
using NoteScribe.Core.Configuration;
using NoteScribe.Core.Notes;
using NoteScribe.Core.Notes.Documents;

namespace NoteScribe.App.ViewModels;

/// <summary>
/// The Notes page: a searchable document library, one open editor, the assistant palette and the
/// revision history. This is the single type the shell binds to.
/// </summary>
/// <remarks>
/// <para>
/// The shell only ever calls <see cref="InitializeAsync"/>, <see cref="TryFlushAsync"/>,
/// <see cref="HasUnsavedChanges"/> and <see cref="DisposeAsync"/>. Everything else on this class
/// exists for <c>NotesWorkspaceView</c> to bind to.
/// </para>
/// <para>
/// No member of this class touches <c>Dispatcher.UIThread</c>: async continuations ride whatever
/// synchronisation context the caller had, which is the UI thread inside the app and the calling
/// thread inside a test harness. That keeps the whole page driveable without a window.
/// </para>
/// </remarks>
public sealed partial class NotesWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private const string UntitledTitle = "Untitled note";
    private const int AiMarkerScanLimit = 60;

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly INoteDocumentStore _store;
    private readonly INoteExporter _exporter;
    private readonly Action<NotificationSeverity, string, string> _notify;

    private CancellationTokenSource? _searchDebounce;
    private CancellationTokenSource? _markerScan;
    private AppSettings _settings = new();
    private bool _initialized;
    private bool _suppressSelection;
    private bool _disposed;

    /// <param name="services">The app-wide service graph. Only Documents, Notes, Settings and AiAssistants are used.</param>
    /// <param name="notify">
    /// <c>notify(severity, title, message)</c> — routed to the shell's notification stack. Every
    /// failure path in this page ends here rather than in an exception dialog.
    /// </param>
    public NotesWorkspaceViewModel(
        AppServices services,
        Action<NotificationSeverity, string, string> notify)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(notify);

        _services = services;
        _notify = notify;
        _store = services.Documents;

        // AppServices does not surface the exporter; the real graph carries one and the fallback is
        // the same implementation the repository uses internally, so both paths render identically.
        _exporter = services.Core?.Exporter ?? new MarkdownNoteExporter();

        AiSettings = new AiSettingsViewModel(services.AiAssistants, services.Settings, notify);
        AiSettings.AssistantChanged += OnAssistantChanged;

        Editor = new NoteEditorViewModel(
            _store,
            () => AiSettings.Assistant,
            () => AiSettings.MaxOutputTokens,
            notify);

        Editor.PropertyChanged += OnEditorPropertyChanged;
        Editor.DocumentSaved += OnDocumentSaved;
    }

    // ---- the shell's contract ---------------------------------------------------------------------

    /// <summary>Loads the document library. Safe to call more than once. Never throws.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            try
            {
                _settings = _services.Settings.Load();
            }
            catch (Exception ex)
            {
                _settings = new AppSettings();
                _notify(NotificationSeverity.Warning, "Could not read assistant settings",
                    $"{ex.Message} Falling back to defaults.");
            }

            AiSettings.Load(_settings.Ai);

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

            if (!_initialized && SelectedDocument is null && Documents.Count > 0)
            {
                SelectedDocument = Documents[0];
            }

            _initialized = true;
        }
        catch (OperationCanceledException)
        {
            // The shell is closing before the page finished loading.
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not open the note library", ex.Message);
        }
    }

    /// <summary>Flushes any unsaved edit. False only when the user must resolve something first.</summary>
    public async Task<bool> TryFlushAsync()
    {
        var ok = await Editor.TryFlushAsync().ConfigureAwait(true);

        try
        {
            await AiSettings.FlushAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Warning, "Could not save assistant settings", ex.Message);
        }

        return ok;
    }

    /// <summary>True when there is an unsaved edit — the shell shows a dot on the Notes nav item.</summary>
    public bool HasUnsavedChanges => Editor.IsDirty;

    // ---- bound state -------------------------------------------------------------------------------

    public NoteEditorViewModel Editor { get; }

    public AiSettingsViewModel AiSettings { get; }

    public ObservableCollection<NoteListItemViewModel> Documents { get; } = [];

    public ObservableCollection<MeetingPickViewModel> Meetings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocuments))]
    [NotifyPropertyChangedFor(nameof(LibrarySummary))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial NoteListItemViewModel? SelectedDocument { get; set; }

    [ObservableProperty] public partial bool IsAiSettingsOpen { get; set; }

    [ObservableProperty] public partial bool IsMeetingPickerOpen { get; set; }

    [ObservableProperty] public partial bool IsMeetingsLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateFromMeetingCommand))]
    public partial MeetingPickViewModel? SelectedMeeting { get; set; }

    public bool HasMeetings => Meetings.Count > 0;

    [ObservableProperty] public partial bool IsRenaming { get; set; }

    [ObservableProperty] public partial string RenameText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsDeleteConfirmOpen { get; set; }

    public bool HasDocuments => Documents.Count > 0;

    public bool HasSelection => SelectedDocument is not null;

    public bool IsSampleData => _services.IsSampleData;

    public string RootDirectory => _store.RootDirectory;

    public string DeleteConfirmText => SelectedDocument is null
        ? string.Empty
        : $"Delete “{SelectedDocument.Title}” and its entire revision history? This cannot be undone.";

    public string LibrarySummary => IsLoading
        ? "loading…"
        : Documents.Count switch
        {
            0 => string.IsNullOrWhiteSpace(SearchText) ? "No notes yet" : "No matches",
            1 => "1 note",
            var n => string.Create(CultureInfo.CurrentCulture, $"{n:N0} notes")
        };

    // ---- library -------------------------------------------------------------------------------------

    [RelayCommand]
    private Task ReloadAsync() => RefreshAsync(CancellationToken.None);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            var documents = await _store
                .ListAsync(string.IsNullOrWhiteSpace(SearchText) ? null : SearchText, cancellationToken)
                .ConfigureAwait(true);

            var previousId = SelectedDocument?.Id;

            _suppressSelection = true;
            try
            {
                Documents.Clear();
                foreach (var document in documents)
                {
                    Documents.Add(new NoteListItemViewModel(document));
                }

                SelectedDocument = previousId is null
                    ? null
                    : Documents.FirstOrDefault(d => string.Equals(d.Id, previousId, StringComparison.Ordinal));
            }
            finally
            {
                _suppressSelection = false;
            }

            OnPropertyChanged(nameof(HasDocuments));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(LibrarySummary));

            QueueAiMarkerScan();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search, or shutting down.
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not list your notes", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();

        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = SearchAfterDelayAsync(cts.Token);
    }

    private async Task SearchAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDebounce, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnSelectedDocumentChanged(NoteListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DeleteConfirmText));
        IsRenaming = false;
        IsDeleteConfirmOpen = false;

        if (_suppressSelection)
        {
            return;
        }

        _ = OpenSelectedAsync(value);
    }

    private async Task OpenSelectedAsync(NoteListItemViewModel? item)
    {
        try
        {
            if (item is null)
            {
                await Editor.CloseAsync().ConfigureAwait(true);
                return;
            }

            // Re-read rather than trusting the list row: another window, or the meeting page, may
            // have moved the head since the list was built.
            var fresh = await _store.LoadAsync(item.Id, CancellationToken.None).ConfigureAwait(true)
                        ?? item.Document;

            item.Update(fresh);
            await Editor.OpenAsync(fresh, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not open that note", ex.Message);
        }
    }

    /// <summary>
    /// Fills in the "last change was AI" dots after the list is already on screen. Reading a
    /// document's revision folder is not free, so this runs behind the render and is capped.
    /// </summary>
    private void QueueAiMarkerScan()
    {
        _markerScan?.Cancel();
        _markerScan?.Dispose();

        var cts = new CancellationTokenSource();
        _markerScan = cts;
        _ = ScanAiMarkersAsync([.. Documents.Take(AiMarkerScanLimit)], cts.Token);
    }

    private async Task ScanAiMarkersAsync(IReadOnlyList<NoteListItemViewModel> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var revisions = await _store.ListRevisionsAsync(item.Id, cancellationToken).ConfigureAwait(true);
                item.IsAiTouched = revisions.Count > 0 && NoteRevisionOrigin.IsAi(revisions[^1].Origin);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A missing or torn history is not worth a banner — the dot simply stays off.
                item.IsAiTouched = false;
            }
        }
    }

    // ---- create / rename / delete ---------------------------------------------------------------------

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        try
        {
            var created = await _store.CreateAsync(
                UntitledTitle,
                _settings.DefaultProject,
                // Seeded rather than empty on purpose: AvaloniaEdit infers its newline from the
                // document, and an empty one makes it insert "\r\n" against a "\n" store.
                $"# {UntitledTitle}\n\n",
                sourceSessionId: null,
                CancellationToken.None).ConfigureAwait(true);

            await AdoptAsync(created).ConfigureAwait(true);
            IsRenaming = true;
            RenameText = created.Title;
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not create the note", ex.Message);
        }
    }

    [RelayCommand]
    private async Task NewFromMeetingAsync()
    {
        IsMeetingPickerOpen = true;
        IsMeetingsLoading = true;

        try
        {
            var sessions = await _services.Notes
                .ListSessionsAsync(new NoteQuery(), CancellationToken.None)
                .ConfigureAwait(true);

            Meetings.Clear();
            SelectedMeeting = null;
            foreach (var session in sessions.Where(s => !s.IsActive).OrderByDescending(s => s.StartedUtc))
            {
                Meetings.Add(new MeetingPickViewModel(session));
            }

            OnPropertyChanged(nameof(HasMeetings));
            SelectedMeeting = Meetings.Count > 0 ? Meetings[0] : null;

            if (Meetings.Count == 0)
            {
                _notify(
                    NotificationSeverity.Info,
                    "No finished meetings yet",
                    "Record and stop a session on the Meeting page, then seed a note from it here.");
            }
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not list your meetings", ex.Message);
        }
        finally
        {
            IsMeetingsLoading = false;
        }
    }

    [RelayCommand]
    private void CloseMeetingPicker() => IsMeetingPickerOpen = false;

    private bool HasMeetingSelection() => SelectedMeeting is not null;

    [RelayCommand(CanExecute = nameof(HasMeetingSelection))]
    private async Task CreateFromMeetingAsync()
    {
        if (SelectedMeeting is not { } pick)
        {
            return;
        }

        IsMeetingPickerOpen = false;

        try
        {
            var entries = await _services.Notes
                .LoadEntriesAsync(pick.Session.Id, CancellationToken.None)
                .ConfigureAwait(true);

            var markdown = _exporter.Render(pick.Session, entries);

            var created = await _store.CreateAsync(
                pick.Session.Title,
                pick.Session.Project,
                markdown,
                pick.Session.Id,
                CancellationToken.None).ConfigureAwait(true);

            await AdoptAsync(created).ConfigureAwait(true);

            _notify(
                NotificationSeverity.Info,
                "Note seeded from a meeting",
                $"“{created.Title}” now holds the transcript. Try Clean up transcript, then Meeting summary.");
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not build a note from that meeting", ex.Message);
        }
    }

    private async Task AdoptAsync(NoteDocument created)
    {
        var item = new NoteListItemViewModel(created);

        _suppressSelection = true;
        try
        {
            Documents.Insert(0, item);
            SelectedDocument = item;
        }
        finally
        {
            _suppressSelection = false;
        }

        OnPropertyChanged(nameof(HasDocuments));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(LibrarySummary));

        await Editor.OpenAsync(created, CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BeginRename()
    {
        if (SelectedDocument is not { } selected)
        {
            return;
        }

        RenameText = selected.Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (SelectedDocument is not { } selected)
        {
            IsRenaming = false;
            return;
        }

        var title = RenameText.Trim();
        IsRenaming = false;

        if (title.Length == 0 || string.Equals(title, selected.Title, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var renamed = await _store.RenameAsync(selected.Id, title, CancellationToken.None).ConfigureAwait(true);
            selected.Update(renamed);

            if (Editor.Document is { } open && string.Equals(open.Id, renamed.Id, StringComparison.Ordinal))
            {
                Editor.AdoptRenamed(renamed);
            }

            OnPropertyChanged(nameof(DeleteConfirmText));
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not rename that note", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RequestDelete()
    {
        OnPropertyChanged(nameof(DeleteConfirmText));
        IsDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsDeleteConfirmOpen = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsDeleteConfirmOpen = false;

        if (SelectedDocument is not { } selected)
        {
            return;
        }

        try
        {
            var wasOpen = Editor.Document is { } open && string.Equals(open.Id, selected.Id, StringComparison.Ordinal);

            if (wasOpen)
            {
                // Drop the in-memory document first so the autosave debounce cannot resurrect it.
                Editor.Abandon();
            }

            await _store.DeleteAsync(selected.Id, CancellationToken.None).ConfigureAwait(true);

            var index = Documents.IndexOf(selected);

            _suppressSelection = true;
            try
            {
                Documents.Remove(selected);
                SelectedDocument = Documents.Count == 0
                    ? null
                    : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
            }
            finally
            {
                _suppressSelection = false;
            }

            OnPropertyChanged(nameof(HasDocuments));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(LibrarySummary));

            await OpenSelectedAsync(SelectedDocument).ConfigureAwait(true);

            _notify(NotificationSeverity.Info, "Note deleted", $"“{selected.Title}” and its history are gone.");
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not delete that note", ex.Message);
        }
    }

    [RelayCommand]
    private void ToggleAiSettings() => IsAiSettingsOpen = !IsAiSettingsOpen;

    [RelayCommand]
    private void OpenNotesFolder()
    {
        try
        {
            SystemShell.OpenDirectory(_store.RootDirectory);
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Warning, "Could not open that folder", ex.Message);
        }
    }

    // ---- editor plumbing --------------------------------------------------------------------------------

    private void OnAssistantChanged(object? sender, IAiAssistant assistant) =>
        OnPropertyChanged(nameof(AiSettings));

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteEditorViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    private void OnDocumentSaved(object? sender, NoteDocument document)
    {
        var item = Documents.FirstOrDefault(d => string.Equals(d.Id, document.Id, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        item.Update(document);

        // A save that reorders "newest first" would yank the row out from under the pointer, so the
        // list order is left alone until the next explicit refresh.
        _ = RefreshAiMarkerAsync(item);
    }

    private async Task RefreshAiMarkerAsync(NoteListItemViewModel item)
    {
        try
        {
            var revisions = await _store.ListRevisionsAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
            item.IsAiTouched = revisions.Count > 0 && NoteRevisionOrigin.IsAi(revisions[^1].Origin);
        }
        catch
        {
            item.IsAiTouched = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _markerScan?.Cancel();
        _markerScan?.Dispose();

        Editor.PropertyChanged -= OnEditorPropertyChanged;
        Editor.DocumentSaved -= OnDocumentSaved;
        AiSettings.AssistantChanged -= OnAssistantChanged;

        await Editor.DisposeAsync().ConfigureAwait(false);
    }
}

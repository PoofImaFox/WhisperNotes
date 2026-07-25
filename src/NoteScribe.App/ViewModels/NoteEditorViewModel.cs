using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.Core.Ai;
using NoteScribe.Core.Notes.Documents;

namespace NoteScribe.App.ViewModels;

/// <summary>How the centre column is split between the editor and the rendered markdown.</summary>
public enum NotePaneMode
{
    Editor,
    Split,
    Preview
}

/// <summary>Which of the two right-hand panes is showing.</summary>
public enum NoteSidePane
{
    Assistant,
    History
}

/// <summary>
/// One open note: its text, its autosave, the assistant runs against it, and its revision history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Text ownership.</b> <see cref="Content"/> mirrors the AvaloniaEdit document character for
/// character — no newline normalisation — so a selection offset taken from the control indexes this
/// string exactly. Normalisation happens once, on the way to
/// <see cref="INoteDocumentStore.SaveAsync"/>, which is also where the dirty comparison is made.
/// </para>
/// <para>
/// <b>The assistant never writes to the document.</b> A run streams into <see cref="PreviewText"/>
/// only. Nothing reaches <see cref="Content"/> — and therefore nothing reaches disk or the revision
/// stack — until the user picks Apply or Insert below.
/// </para>
/// <para>
/// <b>Nothing here touches the Avalonia dispatcher.</b> The view drives text in and out through
/// <see cref="OnEditorTextChanged"/> / <see cref="TextReplaced"/>, which keeps the whole class
/// exercisable from a plain console harness with no window.
/// </para>
/// </remarks>
public sealed partial class NoteEditorViewModel : ObservableObject, IAsyncDisposable
{
    private const string AutosaveLabel = "Edit";
    private const string ManualSaveLabel = "Manual save";
    private const int DiffContextLines = 3;

    private readonly INoteDocumentStore _store;
    private readonly Func<IAiAssistant> _assistant;
    private readonly Func<int> _maxOutputTokens;
    private readonly Action<NotificationSeverity, string, string> _notify;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly StringBuilder _stream = new();
    private readonly List<AiActionViewModel> _actions;

    private CancellationTokenSource? _autosaveDebounce;
    private CancellationTokenSource? _previewDebounce;
    private CancellationTokenSource? _run;

    private string _headContent = string.Empty;
    private string _content = string.Empty;
    private int _selectionStart;
    private int _selectionLength;

    private AiAction? _resultAction;
    private int _resultStart;
    private int _resultLength;
    private bool _resultUsedSelection;
    private bool _suppressDirty;
    private bool _disposed;

    public NoteEditorViewModel(
        INoteDocumentStore store,
        Func<IAiAssistant> assistant,
        Func<int> maxOutputTokens,
        Action<NotificationSeverity, string, string> notify)
    {
        _store = store;
        _assistant = assistant;
        _maxOutputTokens = maxOutputTokens;
        _notify = notify;

        ActionGroups = [.. AiActionCatalog.BuiltIn
            .GroupBy(a => a.Category, StringComparer.Ordinal)
            .Select(g => new AiActionGroupViewModel(
                g.Key,
                [.. g.Select(a => new AiActionViewModel(a, RunActionAsync, CanRunAction))]))];

        _actions = [.. ActionGroups.SelectMany(g => g.Actions)];
    }

    /// <summary>The VM replaced the text wholesale — the view must push it into the editor control.</summary>
    public event EventHandler<string>? TextReplaced;

    /// <summary>Copy-to-clipboard needs a TopLevel, which only the view has.</summary>
    public event EventHandler<string>? CopyRequested;

    /// <summary>A head write landed; the library refreshes the row from this.</summary>
    public event EventHandler<NoteDocument>? DocumentSaved;

    /// <summary>Idle time before an edit is written. Shortened by tests; 1.5s in the app.</summary>
    public TimeSpan AutosaveDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    public IReadOnlyList<AiActionGroupViewModel> ActionGroups { get; }

    public ObservableCollection<NoteRevisionViewModel> Revisions { get; } = [];

    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = [];

    public NoteDocument? Document { get; private set; }

    public bool HasDocument => Document is not null;

    /// <summary>Exactly the editor control's text, newlines and all.</summary>
    public string Content
    {
        get => _content;
        private set
        {
            if (SetProperty(ref _content, value))
            {
                RefreshDirty();
                OnPropertyChanged(nameof(WordCountText));
            }
        }
    }

    /// <summary>Lags <see cref="Content"/> by a short debounce so the preview does not reparse per keystroke.</summary>
    [ObservableProperty] public partial string PreviewMarkdown { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStateText))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStateText))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStateText))]
    public partial DateTimeOffset? LastSavedAt { get; set; }

    [ObservableProperty] public partial string DocumentTitle { get; set; } = string.Empty;

    [ObservableProperty] public partial string DocumentProject { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsSplit))]
    [NotifyPropertyChangedFor(nameof(IsEditorOnly))]
    [NotifyPropertyChangedFor(nameof(IsPreviewOnly))]
    public partial NotePaneMode PaneMode { get; set; } = NotePaneMode.Editor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAssistantPane))]
    [NotifyPropertyChangedFor(nameof(IsHistoryPane))]
    public partial NoteSidePane SidePane { get; set; } = NoteSidePane.Assistant;

    [ObservableProperty] public partial bool IsSidePaneOpen { get; set; } = true;

    [ObservableProperty] public partial string CustomInstruction { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial string PreviewText { get; set; } = string.Empty;

    [ObservableProperty] public partial string PreviewHeader { get; set; } = string.Empty;

    [ObservableProperty] public partial string? PreviewNote { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    public partial string SelectionSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRevision))]
    [NotifyPropertyChangedFor(nameof(DiffHeaderText))]
    public partial NoteRevisionViewModel? SelectedRevision { get; set; }

    [ObservableProperty] public partial string DiffSummaryText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsHistoryLoading { get; set; }

    public bool IsEditorVisible => PaneMode is NotePaneMode.Editor or NotePaneMode.Split;

    public bool IsPreviewVisible => PaneMode is NotePaneMode.Preview or NotePaneMode.Split;

    public bool IsEditorOnly => PaneMode == NotePaneMode.Editor;

    public bool IsSplit => PaneMode == NotePaneMode.Split;

    public bool IsPreviewOnly => PaneMode == NotePaneMode.Preview;

    public bool IsAssistantPane => SidePane == NoteSidePane.Assistant;

    public bool IsHistoryPane => SidePane == NoteSidePane.History;

    public bool HasPreview => PreviewText.Length > 0;

    public bool CanStop => IsRunning;

    public bool HasSelection => _selectionLength > 0;

    public bool HasSelectedRevision => SelectedRevision is not null;

    public bool HasRevisions => Revisions.Count > 0;

    public string TargetText => HasSelection ? "selection" : "whole note";

    public string DiffHeaderText => SelectedRevision is null
        ? string.Empty
        : $"{SelectedRevision.TimestampText} → current";

    public string WordCountText
    {
        get
        {
            var words = 0;
            var inWord = false;
            foreach (var c in _content)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    words++;
                }
            }

            return string.Create(CultureInfo.CurrentCulture, $"{words:N0} words");
        }
    }

    public string SaveStateText
    {
        get
        {
            if (!HasDocument)
            {
                return string.Empty;
            }

            if (IsSaving)
            {
                return "Saving…";
            }

            if (IsDirty)
            {
                return "Unsaved changes";
            }

            return LastSavedAt is { } saved
                ? string.Create(CultureInfo.CurrentCulture, $"Saved {saved.ToLocalTime():HH:mm:ss}")
                : "Saved";
        }
    }

    // ---- document lifecycle --------------------------------------------------------------------

    /// <summary>Shows a document. Any pending edit on the outgoing one is flushed first.</summary>
    public async Task OpenAsync(NoteDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Document is not null && !string.Equals(Document.Id, document.Id, StringComparison.Ordinal))
        {
            await TryFlushAsync().ConfigureAwait(true);
        }

        CancelRun();
        CancelAutosave();

        _suppressDirty = true;
        try
        {
            Document = document;
            _headContent = document.Content;
            DocumentTitle = document.Title;
            DocumentProject = document.Project ?? string.Empty;
            Content = document.Content;
            LastSavedAt = document.UpdatedUtc;
            PreviewMarkdown = document.Content;
        }
        finally
        {
            _suppressDirty = false;
        }

        IsDirty = false;
        ClearPreview();
        SetSelection(0, 0);

        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(SaveStateText));

        TextReplaced?.Invoke(this, document.Content);

        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Drops the open document after flushing it.</summary>
    public async Task CloseAsync()
    {
        await TryFlushAsync().ConfigureAwait(true);

        CancelRun();
        CancelAutosave();

        _suppressDirty = true;
        try
        {
            Document = null;
            _headContent = string.Empty;
            DocumentTitle = string.Empty;
            DocumentProject = string.Empty;
            Content = string.Empty;
            PreviewMarkdown = string.Empty;
        }
        finally
        {
            _suppressDirty = false;
        }

        IsDirty = false;
        Revisions.Clear();
        DiffLines.Clear();
        SelectedRevision = null;
        ClearPreview();

        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HasRevisions));
        OnPropertyChanged(nameof(SaveStateText));

        TextReplaced?.Invoke(this, string.Empty);
    }

    /// <summary>Adopts a title change made in the library, without marking the body dirty.</summary>
    public void AdoptRenamed(NoteDocument renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);

        if (Document is not { } document || !string.Equals(document.Id, renamed.Id, StringComparison.Ordinal))
        {
            return;
        }

        _suppressDirty = true;
        try
        {
            // Keep the live body: RenameAsync only rewrites metadata, and the editor may well be
            // holding newer text than the copy the rename returned.
            Document = renamed with { Content = document.Content };
            DocumentTitle = renamed.Title;
            DocumentProject = renamed.Project ?? string.Empty;
        }
        finally
        {
            _suppressDirty = false;
        }

        RefreshDirty();
    }

    /// <summary>
    /// Drops the open document without saving. Used only when the document is being deleted — every
    /// other path flushes.
    /// </summary>
    public void Abandon()
    {
        CancelRun();
        CancelAutosave();

        _suppressDirty = true;
        try
        {
            Document = null;
            _headContent = string.Empty;
            DocumentTitle = string.Empty;
            DocumentProject = string.Empty;
            Content = string.Empty;
            PreviewMarkdown = string.Empty;
        }
        finally
        {
            _suppressDirty = false;
        }

        IsDirty = false;
        Revisions.Clear();
        DiffLines.Clear();
        SelectedRevision = null;
        ClearPreview();

        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HasRevisions));
        OnPropertyChanged(nameof(SaveStateText));

        TextReplaced?.Invoke(this, string.Empty);
    }

    /// <summary>Called by the view on every AvaloniaEdit <c>TextChanged</c>.</summary>
    public void OnEditorTextChanged(string text)
    {
        if (Document is null || string.Equals(text, _content, StringComparison.Ordinal))
        {
            return;
        }

        Content = text;
        QueuePreview();
    }

    /// <summary>Called by the view whenever the caret or selection moves.</summary>
    public void SetSelection(int start, int length)
    {
        var max = _content.Length;
        _selectionStart = Math.Clamp(start, 0, max);
        _selectionLength = Math.Clamp(length, 0, max - _selectionStart);

        SelectionSummary = _selectionLength == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"{_selectionLength:N0} chars selected");

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(TargetText));
        RaiseActionsCanExecuteChanged();
    }

    // ---- saving ---------------------------------------------------------------------------------

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(ManualSaveLabel, NoteRevisionOrigin.User);

    /// <summary>
    /// Writes anything outstanding. False means the caller should not proceed as if the text were
    /// safe — the only reason is a failed write, which is surfaced as a notification.
    /// </summary>
    public async Task<bool> TryFlushAsync()
    {
        CancelAutosave();

        if (Document is null || !IsDirty)
        {
            return true;
        }

        return await SaveCoreAsync(AutosaveLabel, NoteRevisionOrigin.User).ConfigureAwait(true);
    }

    private async Task<bool> SaveCoreAsync(string label, string origin)
    {
        if (Document is not { } document)
        {
            return true;
        }

        await _saveGate.WaitAsync().ConfigureAwait(true);
        IsSaving = true;

        try
        {
            var body = Normalize(_content);
            var title = string.IsNullOrWhiteSpace(DocumentTitle) ? document.Title : DocumentTitle.Trim();
            var project = string.IsNullOrWhiteSpace(DocumentProject) ? null : DocumentProject.Trim();

            var unchanged = string.Equals(body, document.Content, StringComparison.Ordinal)
                            && string.Equals(title, document.Title, StringComparison.Ordinal)
                            && string.Equals(project, document.Project, StringComparison.Ordinal);

            if (unchanged)
            {
                IsDirty = false;
                return true;
            }

            var saved = await _store.SaveAsync(
                document with { Content = body, Title = title, Project = project },
                label,
                origin,
                CancellationToken.None).ConfigureAwait(true);

            Document = saved;
            _headContent = saved.Content;
            LastSavedAt = saved.UpdatedUtc;
            RefreshDirty();

            DocumentSaved?.Invoke(this, saved);
            return true;
        }
        catch (Exception ex)
        {
            _notify(
                NotificationSeverity.Error,
                "Could not save the note",
                $"{ex.Message} Your text is still in the editor — copy it out before closing if this keeps happening.");
            return false;
        }
        finally
        {
            IsSaving = false;
            _saveGate.Release();

            // History is a projection of what just landed on disk.
            await RefreshHistoryAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void RefreshDirty()
    {
        if (_suppressDirty)
        {
            return;
        }

        if (Document is not { } document)
        {
            IsDirty = false;
            return;
        }

        var project = string.IsNullOrWhiteSpace(DocumentProject) ? null : DocumentProject.Trim();
        var title = string.IsNullOrWhiteSpace(DocumentTitle) ? document.Title : DocumentTitle.Trim();

        IsDirty = !string.Equals(Normalize(_content), document.Content, StringComparison.Ordinal)
                  || !string.Equals(title, document.Title, StringComparison.Ordinal)
                  || !string.Equals(project, document.Project, StringComparison.Ordinal);

        if (IsDirty)
        {
            QueueAutosave();
        }
    }

    partial void OnDocumentProjectChanged(string value) => RefreshDirty();

    partial void OnDocumentTitleChanged(string value) => RefreshDirty();

    private void QueueAutosave()
    {
        CancelAutosave();

        var cts = new CancellationTokenSource();
        _autosaveDebounce = cts;
        _ = AutosaveAsync(cts.Token);
    }

    private async Task AutosaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutosaveDelay, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || !IsDirty)
        {
            return;
        }

        await SaveCoreAsync(AutosaveLabel, NoteRevisionOrigin.User).ConfigureAwait(true);
    }

    private void CancelAutosave()
    {
        _autosaveDebounce?.Cancel();
        _autosaveDebounce?.Dispose();
        _autosaveDebounce = null;
    }

    private void QueuePreview()
    {
        _previewDebounce?.Cancel();
        _previewDebounce?.Dispose();

        var cts = new CancellationTokenSource();
        _previewDebounce = cts;
        _ = PreviewAfterDelayAsync(cts.Token);
    }

    private async Task PreviewAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(220), cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PreviewMarkdown = _content;
    }

    // ---- view mode ------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowEditor() => PaneMode = NotePaneMode.Editor;

    [RelayCommand]
    private void ShowSplit() => PaneMode = NotePaneMode.Split;

    [RelayCommand]
    private void ShowPreview()
    {
        PreviewMarkdown = _content;
        PaneMode = NotePaneMode.Preview;
    }

    [RelayCommand]
    private void ShowAssistant()
    {
        SidePane = NoteSidePane.Assistant;
        IsSidePaneOpen = true;
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        SidePane = NoteSidePane.History;
        IsSidePaneOpen = true;
        await RefreshHistoryAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleSidePane() => IsSidePaneOpen = !IsSidePaneOpen;

    partial void OnPaneModeChanged(NotePaneMode value)
    {
        if (value != NotePaneMode.Editor)
        {
            PreviewMarkdown = _content;
        }
    }

    // ---- assistant -------------------------------------------------------------------------------

    private bool CanRunAction() => HasDocument && !IsRunning;

    private void RaiseActionsCanExecuteChanged()
    {
        foreach (var action in _actions)
        {
            action.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Streams an action's answer into the preview pane. Nothing is written to the document here;
    /// that only ever happens in <see cref="ApplyPreviewAsync"/> or <see cref="InsertPreviewAsync"/>.
    /// </summary>
    private async Task RunActionAsync(AiActionViewModel item)
    {
        if (Document is not { } document || IsRunning)
        {
            return;
        }

        var action = item.Action;

        var useSelection = _selectionLength > 0 && action.Scope != AiActionScope.WholeDocument;
        if (action.Scope == AiActionScope.Selection && !useSelection)
        {
            _notify(
                NotificationSeverity.Info,
                $"{action.Name} needs a selection",
                "Highlight the text you want reshaped, then run the action again.");
            return;
        }

        var target = useSelection
            ? _content.Substring(_selectionStart, _selectionLength)
            : _content;

        if (string.IsNullOrWhiteSpace(target))
        {
            _notify(
                NotificationSeverity.Info,
                "Nothing to work on",
                "This note is empty — write or dictate something first.");
            return;
        }

        var instruction = CustomInstruction.Trim();
        if (action.NeedsInstruction && instruction.Length == 0)
        {
            SidePane = NoteSidePane.Assistant;
            IsSidePaneOpen = true;
            _notify(
                NotificationSeverity.Info,
                "Type an instruction first",
                "Custom instruction runs whatever you write in the box above the actions.");
            return;
        }

        var assistant = _assistant();
        if (!assistant.IsConfigured)
        {
            _notify(
                NotificationSeverity.Warning,
                "Assistant not configured",
                assistant.ConfigurationHint ?? "Open the assistant settings and pick a provider that is reachable.");
            return;
        }

        // {{content}} is always the target text — the selection when there is one, otherwise the
        // whole note. {{selection}} is supplied too, for any action that ever wants both.
        var prompt = AiActionCatalog.Render(action.UserPromptTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content"] = target,
            ["selection"] = useSelection ? target : string.Empty,
            ["title"] = document.Title,
            ["project"] = document.Project ?? string.Empty,
            ["instruction"] = instruction,
        });

        var request = new AiRequest(action.SystemPrompt, [AiMessage.User(prompt)], _maxOutputTokens());

        _resultAction = action;
        _resultStart = _selectionStart;
        _resultLength = _selectionLength;
        _resultUsedSelection = useSelection;

        _stream.Clear();
        PreviewText = string.Empty;
        PreviewNote = null;
        PreviewHeader = $"{action.Icon}  {action.Name} · {(useSelection ? "selection" : "whole note")}";

        var cts = new CancellationTokenSource();
        _run = cts;

        IsRunning = true;
        item.IsRunning = true;
        SidePane = NoteSidePane.Assistant;
        IsSidePaneOpen = true;
        RaiseActionsCanExecuteChanged();

        try
        {
            await foreach (var fragment in assistant.StreamAsync(request, cts.Token).ConfigureAwait(true))
            {
                _stream.Append(fragment);
                PreviewText = _stream.ToString();
            }

            PreviewText = _stream.ToString().Trim();

            if (PreviewText.Length == 0)
            {
                PreviewNote = "The model returned nothing. Try again, or pick a different model.";
            }
        }
        catch (OperationCanceledException)
        {
            PreviewText = _stream.ToString().Trim();
            PreviewNote = PreviewText.Length == 0
                ? "Stopped before anything arrived."
                : "Stopped early — this is a partial answer.";
        }
        catch (AiException ex)
        {
            // The provider's own words, verbatim: "model not pulled", "invalid key", "connection refused".
            _notify(NotificationSeverity.Warning, $"{action.Name} failed", ex.Message);
            PreviewNote = ex.Message;
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, $"{action.Name} failed", ex.Message);
            PreviewNote = ex.Message;
        }
        finally
        {
            IsRunning = false;
            item.IsRunning = false;
            cts.Dispose();
            if (ReferenceEquals(_run, cts))
            {
                _run = null;
            }

            RaiseActionsCanExecuteChanged();
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    [RelayCommand]
    private void Stop() => CancelRun();

    private void CancelRun()
    {
        try
        {
            _run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the null check and the cancel.
        }
    }

    /// <summary>
    /// Replaces the target text with the answer and saves under <c>ai:&lt;actionId&gt;</c>, so the
    /// pre-change body is on the revision stack and one click undoes the whole thing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasPreview))]
    private async Task ApplyPreviewAsync()
    {
        if (Document is null || _resultAction is not { } action || PreviewText.Length == 0)
        {
            return;
        }

        // Flush the user's own typing first so it becomes its own revision rather than being
        // folded into — and lost behind — the AI change.
        if (!await TryFlushAsync().ConfigureAwait(true))
        {
            return;
        }

        var replacement = PreviewText;
        var updated = _resultUsedSelection && _resultLength > 0 && _resultStart + _resultLength <= _content.Length
            ? string.Concat(_content.AsSpan(0, _resultStart), replacement, _content.AsSpan(_resultStart + _resultLength))
            : replacement;

        await CommitAsync(updated, action, $"{action.Name} (applied)").ConfigureAwait(true);
    }

    /// <summary>Keeps the original text and drops the answer in beneath it.</summary>
    [RelayCommand(CanExecute = nameof(HasPreview))]
    private async Task InsertPreviewAsync()
    {
        if (Document is null || _resultAction is not { } action || PreviewText.Length == 0)
        {
            return;
        }

        if (!await TryFlushAsync().ConfigureAwait(true))
        {
            return;
        }

        var at = _resultUsedSelection && _resultLength > 0
            ? Math.Min(_content.Length, _resultStart + _resultLength)
            : _content.Length;

        var head = _content[..at];
        var tail = _content[at..];
        var separator = head.EndsWith('\n') ? "\n" : "\n\n";

        var updated = string.Concat(head, separator, PreviewText, tail.Length == 0 ? "\n" : "\n\n", tail);

        await CommitAsync(updated, action, $"{action.Name} (inserted)").ConfigureAwait(true);
    }

    private async Task CommitAsync(string updated, AiAction action, string message)
    {
        Content = updated;
        _selectionStart = 0;
        _selectionLength = 0;
        SelectionSummary = string.Empty;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(TargetText));

        TextReplaced?.Invoke(this, updated);
        PreviewMarkdown = updated;

        CancelAutosave();

        var ok = await SaveCoreAsync(action.Name, NoteRevisionOrigin.Ai(action.Id)).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        ClearPreview();
        _notify(NotificationSeverity.Info, message, "The previous text is in History — one click restores it.");
    }

    [RelayCommand(CanExecute = nameof(HasPreview))]
    private void CopyPreview() => CopyRequested?.Invoke(this, PreviewText);

    [RelayCommand(CanExecute = nameof(HasPreview))]
    private void DiscardPreview() => ClearPreview();

    private void ClearPreview()
    {
        _stream.Clear();
        PreviewText = string.Empty;
        PreviewHeader = string.Empty;
        PreviewNote = null;
        _resultAction = null;
        _resultStart = 0;
        _resultLength = 0;
        _resultUsedSelection = false;
    }

    partial void OnPreviewTextChanged(string value)
    {
        ApplyPreviewCommand.NotifyCanExecuteChanged();
        InsertPreviewCommand.NotifyCanExecuteChanged();
        CopyPreviewCommand.NotifyCanExecuteChanged();
        DiscardPreviewCommand.NotifyCanExecuteChanged();
    }

    // ---- history + revert -------------------------------------------------------------------------

    [RelayCommand]
    private Task ReloadHistoryAsync() => RefreshHistoryAsync(CancellationToken.None);

    /// <summary>
    /// Rebuilds the history list newest-first. The store returns oldest-first with entry zero as the
    /// original, and each entry's <c>+N −M</c> is measured against whatever superseded it — the next
    /// revision, or the live head for the newest one.
    /// </summary>
    public async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        if (Document is not { } document)
        {
            Revisions.Clear();
            DiffLines.Clear();
            OnPropertyChanged(nameof(HasRevisions));
            return;
        }

        IsHistoryLoading = true;
        try
        {
            var revisions = await _store.ListRevisionsAsync(document.Id, cancellationToken).ConfigureAwait(true);
            var selectedId = SelectedRevision?.Id;

            Revisions.Clear();

            for (var i = revisions.Count - 1; i >= 0; i--)
            {
                var successor = i == revisions.Count - 1 ? _headContent : revisions[i + 1].Content;
                Revisions.Add(new NoteRevisionViewModel(revisions[i], successor, isOriginal: i == 0));
            }

            OnPropertyChanged(nameof(HasRevisions));

            SelectedRevision = selectedId is null
                ? null
                : Revisions.FirstOrDefault(r => string.Equals(r.Id, selectedId, StringComparison.Ordinal));
        }
        catch (OperationCanceledException)
        {
            // Switching documents.
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Warning, "Could not read the note's history", ex.Message);
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    partial void OnSelectedRevisionChanged(NoteRevisionViewModel? value)
    {
        RestoreRevisionCommand.NotifyCanExecuteChanged();
        BuildDiff(value);
    }

    private void BuildDiff(NoteRevisionViewModel? revision)
    {
        DiffLines.Clear();

        if (revision is null)
        {
            DiffSummaryText = string.Empty;
            return;
        }

        var head = Normalize(_content);
        var lines = TextDiff.Unified(revision.Revision.Content, head, DiffContextLines);

        if (lines.Count == 0)
        {
            DiffSummaryText = "Identical to the text on screen.";
            return;
        }

        DiffLine? previous = null;
        foreach (var line in lines)
        {
            if (previous is not null && TextDiff.IsElision(previous, line))
            {
                DiffLines.Add(DiffLineViewModel.Elision());
            }

            DiffLines.Add(new DiffLineViewModel(line));
            previous = line;
        }

        var (added, removed) = TextDiff.Stat(revision.Revision.Content, head);
        DiffSummaryText = string.Create(
            CultureInfo.InvariantCulture,
            $"+{added} −{removed} since this version");
    }

    /// <summary>
    /// Restores the selected snapshot as the new head. The store records the revert as a revision of
    /// its own, so this is itself undoable.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedRevision))]
    private async Task RestoreRevisionAsync()
    {
        if (Document is not { } document || SelectedRevision is not { } revision)
        {
            return;
        }

        // Anything typed since the last save becomes its own revision before we move the head, so
        // "restore" can never be the thing that eats a paragraph.
        if (!await TryFlushAsync().ConfigureAwait(true))
        {
            return;
        }

        CancelAutosave();

        try
        {
            var reverted = await _store
                .RevertAsync(document.Id, revision.Id, CancellationToken.None)
                .ConfigureAwait(true);

            _suppressDirty = true;
            try
            {
                Document = reverted;
                _headContent = reverted.Content;
                DocumentTitle = reverted.Title;
                DocumentProject = reverted.Project ?? string.Empty;
                Content = reverted.Content;
                LastSavedAt = reverted.UpdatedUtc;
                PreviewMarkdown = reverted.Content;
            }
            finally
            {
                _suppressDirty = false;
            }

            IsDirty = false;

            TextReplaced?.Invoke(this, reverted.Content);
            DocumentSaved?.Invoke(this, reverted);

            _notify(
                NotificationSeverity.Info,
                "Restored an earlier version",
                $"“{revision.Label}” from {revision.TimestampText} is now the note. The version you just left is in History.");

            await RefreshHistoryAsync(CancellationToken.None).ConfigureAwait(true);
            SelectedRevision = null;
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Error, "Could not restore that version", ex.Message);
        }
    }

    [RelayCommand]
    private void ClearRevisionSelection() => SelectedRevision = null;

    // ---- plumbing ---------------------------------------------------------------------------------

    /// <summary>
    /// Collapses <c>\r\n</c> and lone <c>\r</c> to <c>\n</c>, matching the store's on-disk
    /// convention so a round trip never shows a whole-file diff.
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains('\r', StringComparison.Ordinal)
            ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : value;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CancelRun();
        CancelAutosave();
        _previewDebounce?.Cancel();
        _previewDebounce?.Dispose();

        await TryFlushAsync().ConfigureAwait(false);

        _saveGate.Dispose();
    }
}

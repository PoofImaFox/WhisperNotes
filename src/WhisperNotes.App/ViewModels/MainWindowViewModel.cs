using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperNotes.App.Composition;
using WhisperNotes.App.Shell;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.App.ViewModels;

/// <summary>The shell's top-level pages.</summary>
public enum ShellPage
{
    /// <summary>Live capture: the browser tree plus the transcript log.</summary>
    Meeting,

    /// <summary>The standalone note workspace (editor, preview, AI actions, revisions).</summary>
    Notes,

    /// <summary>Audio endpoints captured and transcribed together.</summary>
    Inputs
}

/// <summary>
/// The shell: it owns the window's regions, the state the toolbar and status bar bind to, and the
/// wiring between them. It implements almost none of it.
/// </summary>
/// <remarks>
/// <para>
/// The work lives in collaborators under <c>WhisperNotes.App.Shell</c>, each named for what it owns:
/// <see cref="RecordingSessionController"/> (a meeting, start to finalised),
/// <see cref="VideoImportController"/> (a recording already on disk),
/// <see cref="ShellSettings"/> (the settings round-trip),
/// <see cref="ShellNotificationCenter"/> (the banner stack) and
/// <see cref="InterimStatusReporter"/> (the transcript's liveness line).
/// </para>
/// <para>
/// Those collaborators drive this class back through <see cref="IRecordingShell"/> and
/// <see cref="IVideoImportShell"/>, which are deliberately subsets of members that already exist
/// here — implementing them adds nothing to the surface the views bind to.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel
    : ObservableObject, IAsyncDisposable, IRecordingShell, IVideoImportShell
{
    private readonly AppServices _services;
    private readonly ShellNotificationCenter _notifications;
    private readonly ShellSettings _settings;
    private readonly InterimStatusReporter _interim;
    private readonly RecordingSessionController _recording;
    private readonly VideoImportController _videoImport;
    private bool _settingsLoaded;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        _notifications = new ShellNotificationCenter(() => OnPropertyChanged(nameof(HasNotifications)));
        _settings = new ShellSettings(services.Settings, _notifications);

        Capture = new CaptureViewModel(
            services.ChannelEnumerator,
            services.CaptureSourceFactory,
            services.ModelStore,
            _notifications.Post);

        Document = new SessionDocumentViewModel(services.Notes, _notifications.Post);
        Browser = new NotesBrowserViewModel(services.Notes, _notifications.Post);

        // Built eagerly and kept for the life of the window: the Notes page holds an editor with
        // an undo stack and unsaved edits, so it must never be rebuilt by a page switch.
        Notes = new NotesWorkspaceViewModel(services, _notifications.Report);
        Inputs = new InputSettingsViewModel(services.ChannelEnumerator, services.Settings, _notifications.Report);

        // The toolbar meter is a pre-flight for the whole enabled set, not for one picker row.
        Capture.MonitoredChannelsProvider = () => Inputs.EnabledChannels;

        _interim = new InterimStatusReporter(Document, Capture.Meter, this);
        _recording = new RecordingSessionController(
            services, this, Capture, Document, Browser, Inputs, _settings, _notifications, _interim);
        _videoImport = new VideoImportController(
            services, this, Capture, Document, Browser, _settings, _notifications);

        Browser.SessionActivated += OnSessionActivated;
        Capture.PropertyChanged += OnCapturePropertyChanged;
        Inputs.InputsChanged += OnInputsChanged;

        _interim.Start();
    }

    public CaptureViewModel Capture { get; }

    public SessionDocumentViewModel Document { get; }

    public NotesBrowserViewModel Browser { get; }

    /// <summary>The Notes page. Constructed once; the page switch only toggles its visibility.</summary>
    public NotesWorkspaceViewModel Notes { get; }

    /// <summary>Durable multi-input configuration used by the next recording.</summary>
    public InputSettingsViewModel Inputs { get; }

    public ObservableCollection<NotificationViewModel> Notifications => _notifications.Items;

    /// <summary>
    /// Which page the rail is showing.
    /// <para>
    /// Both pages are built once and stay in the visual tree; switching only flips
    /// <c>IsVisible</c>. That is deliberate: the Meeting page owns a transcript
    /// <c>ScrollViewer</c> whose offset is view state, and an audio meter driven by a 60 ms
    /// timer. Rebuilding the page — which a <c>ContentControl</c> + <c>DataTemplate</c> would do
    /// on every switch — would throw both away mid-recording.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMeetingPage))]
    [NotifyPropertyChangedFor(nameof(IsNotesPage))]
    [NotifyPropertyChangedFor(nameof(IsInputsPage))]
    public partial ShellPage SelectedPage { get; set; }

    public bool IsMeetingPage => SelectedPage == ShellPage.Meeting;

    public bool IsNotesPage => SelectedPage == ShellPage.Notes;

    public bool IsInputsPage => SelectedPage == ShellPage.Inputs;

    /// <summary>
    /// True when the app fell back to in-process fakes after a startup failure. Constant for the
    /// lifetime of the process, so the chip it drives can sit in the layout without ever causing
    /// a reflow.
    /// </summary>
    public bool IsSampleData => _services.IsSampleData;

    public string SampleDataText => _services.SampleDataReason is { Length: > 0 } reason
        ? $"Devices, transcription and notes are simulated — nothing here is a real recording. Core services failed to start: {reason}"
        : "Devices, transcription and notes are simulated — nothing here is a real recording.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyPropertyChangedFor(nameof(RecordButtonHint))]
    [NotifyPropertyChangedFor(nameof(CanImportVideo))]
    [NotifyPropertyChangedFor(nameof(CanEditSessionMetadata))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyPropertyChangedFor(nameof(CanImportVideo))]
    [NotifyPropertyChangedFor(nameof(CanEditSessionMetadata))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    public partial bool IsTransitioning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportVideo))]
    [NotifyPropertyChangedFor(nameof(CanEditSessionMetadata))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelVideoImportCommand))]
    public partial bool IsImportingVideo { get; set; }

    [ObservableProperty] public partial double ImportVideoProgress { get; set; }

    [ObservableProperty] public partial bool IsImportVideoProgressIndeterminate { get; set; } = true;

    [ObservableProperty] public partial string ImportVideoStatusText { get; set; } = "";

    [ObservableProperty] public partial string StatusMessage { get; set; } = "Ready.";

    public bool HasNotifications => _notifications.HasAny;

    public string WindowTitle => "WhisperNotes";

    public string NotesRootPath => _services.Notes.RootDirectory;

    public string SettingsPath => _settings.SettingsPath;

    public string FfmpegStatusText => _services.Media.IsAvailable
        ? $"ffmpeg: {_services.Media.FfmpegPath}"
        : "ffmpeg: not found (video import unavailable)";

    public bool CanImportVideo =>
        !IsRecording && !IsTransitioning && !IsImportingVideo && _services.Media.IsAvailable;

    public bool CanEditSessionMetadata => !IsRecording && !IsTransitioning && !IsImportingVideo;

    public string RecordButtonText => IsTransitioning
        ? IsRecording ? "Stopping…" : "Starting…"
        : IsRecording ? "Stop" : "Start recording";

    public string RecordButtonHint => IsRecording
        ? "Finalises the session and renders notes.md"
        : "Creates a session and starts every enabled input in parallel";

    /// <summary>Loads settings, lists past sessions and starts pre-flight monitoring.</summary>
    public async Task InitializeAsync()
    {
        _settings.Initialize();

        Inputs.Initialize();
        Capture.SelectChannel(
            Inputs.EnabledInputs.FirstOrDefault()?.Channel.Id
            ?? _settings.LastChannelId);
        Capture.SelectModel(_settings.Model);
        Capture.Project = _settings.DefaultProject ?? string.Empty;
        _settingsLoaded = true;

        OnPropertyChanged(nameof(NotesRootPath));
        OnPropertyChanged(nameof(SettingsPath));
        OnPropertyChanged(nameof(FfmpegStatusText));

        // The sample-data warning is deliberately NOT a notification any more. It can never be
        // dismissed and never goes away, so as a floating toast it would hover over the content
        // for the whole session and permanently occupy a slot in the four-deep banner stack.
        // It is rendered instead as a docked one-line chip driven by IsSampleData.

        await Browser.RefreshAsync().ConfigureAwait(true);
        await Capture.StartMonitoringAsync().ConfigureAwait(true);

        try
        {
            await Notes.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Contractually it never throws; the guard exists so a Notes-side regression can
            // never stop the meeting side of the app from coming up.
            _notifications.Post("Notes are unavailable", ex.Message, NotificationSeverity.Warning);
        }

        StatusMessage = "Ready.";
    }

    /// <summary>Rail / Ctrl+1 / Ctrl+2 / Ctrl+3. Pure view state — nothing is torn down or rebuilt.</summary>
    [RelayCommand]
    private void GoToPage(ShellPage page) => SelectedPage = page;

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private Task ToggleRecordingAsync() => _recording.ToggleAsync();

    private bool CanToggleRecording() => !IsTransitioning && !IsImportingVideo;

    /// <summary>Entry point for the view's native file picker, which owns the <c>StorageProvider</c>.</summary>
    public Task ImportVideoAsync(string inputPath) => _videoImport.ImportAsync(inputPath);

    [RelayCommand(CanExecute = nameof(CanCancelVideoImport))]
    private void CancelVideoImport() => _videoImport.Cancel();

    private bool CanCancelVideoImport() => IsImportingVideo;

    public void ReportVideoPickerFailure(Exception exception) =>
        _notifications.Post("Could not open the video picker", exception.Message, NotificationSeverity.Error);

    [RelayCommand]
    private void OpenNotesRoot() => FolderLauncher.Open(_services.Notes.RootDirectory, _notifications);

    /// <summary>
    /// Opening a past session takes over the transcript pane, so it is refused while either
    /// capture path owns it — except for the import's own session, which is already showing.
    /// </summary>
    private async void OnSessionActivated(object? sender, NoteSession session)
    {
        if (IsRecording ||
            (IsImportingVideo &&
             !string.Equals(Document.Session?.Id, session.Id, StringComparison.Ordinal)))
        {
            _notifications.Post(
                IsRecording ? "Still recording" : "Video import in progress",
                IsRecording
                    ? "Stop the current session before opening an older one — the live notes stay on screen."
                    : "Cancel or finish the import before opening another session.",
                NotificationSeverity.Info);
            return;
        }

        try
        {
            var entries = await _services.Notes.LoadEntriesAsync(session.Id, CancellationToken.None).ConfigureAwait(true);
            Document.ShowReadOnlySession(session, entries);
            StatusMessage = $"Viewing “{session.Title}”.";
        }
        catch (Exception ex)
        {
            _notifications.Post("Could not open that session", ex.Message, NotificationSeverity.Error);
        }
    }

    private void OnInputsChanged(object? sender, EventArgs e)
    {
        if (Inputs.EnabledInputs.FirstOrDefault() is { } primary)
        {
            Capture.SelectChannel(primary.Channel.Id);
        }
        else
        {
            Capture.SelectedChannel = null;
        }

        // Adding, removing, enabling or re-pointing an input changes the set the meter taps, so
        // the monitor is restarted against the new set (and stopped when it is empty).
        _ = Capture.StartMonitoringAsync();
    }

    private void OnCapturePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(CaptureViewModel.SelectedModel):
                if (Capture.SelectedModel is { } model)
                {
                    _settings.SetModel(model.Size);
                    _settings.QueueSave();
                }

                break;
            case nameof(CaptureViewModel.Project):
                _settings.SetDefaultProject(string.IsNullOrWhiteSpace(Capture.Project) ? null : Capture.Project.Trim());
                _settings.QueueSave();
                break;
        }
    }

    /// <summary>Finalises anything in flight so closing the window never loses a meeting.</summary>
    public async Task ShutdownAsync()
    {
        _videoImport.BeginShutdown();
        _interim.Stop();

        await _recording.ShutdownAsync().ConfigureAwait(true);
        await _videoImport.WaitForCompletionAsync().ConfigureAwait(true);

        await Capture.StopMonitoringAsync().ConfigureAwait(true);

        // A half-typed note is as much the user's work as a meeting is; flush before settings so
        // a slow settings write can never be what loses it.
        try
        {
            if (!await Notes.TryFlushAsync().ConfigureAwait(true))
            {
                SelectedPage = ShellPage.Notes;
                _notifications.Post(
                    "A note could not be saved",
                    "The Notes page still has an unresolved edit. It is shown now so you can deal with it.",
                    NotificationSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            _notifications.Post("Could not save the open note", ex.Message, NotificationSeverity.Warning);
        }

        await Inputs.FlushAsync().ConfigureAwait(true);
        await _settings.SaveAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        _interim.Stop();
        Browser.SessionActivated -= OnSessionActivated;
        Capture.PropertyChanged -= OnCapturePropertyChanged;
        Inputs.InputsChanged -= OnInputsChanged;
        await _settings.DisposeAsync().ConfigureAwait(true);
        await _recording.DisposeAsync().ConfigureAwait(true);
        await _videoImport.DisposeAsync().ConfigureAwait(true);
        await Notes.DisposeAsync().ConfigureAwait(false);
        await Capture.DisposeAsync().ConfigureAwait(false);
    }
}

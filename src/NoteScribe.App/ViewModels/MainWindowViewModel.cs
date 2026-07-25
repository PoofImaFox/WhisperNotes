using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.App.Composition;
using NoteScribe.App.Services;
using NoteScribe.Core.Audio;
using NoteScribe.Core.Configuration;
using NoteScribe.Core.Notes;

namespace NoteScribe.App.ViewModels;

/// <summary>Shell coordinator: owns the recording lifecycle and everything the three regions share.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _interimTimer;
    private AppSettings _settings = new();
    private CancellationTokenSource? _recordingCts;
    private CancellationTokenSource? _settingsDebounce;
    private Task? _recordingTask;
    private Exception? _pipelineFault;
    private NoteSession? _liveSession;
    private DateTimeOffset _lastSegmentAt;
    private bool _settingsLoaded;
    private bool _stopInProgress;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;

        Capture = new CaptureViewModel(
            services.ChannelEnumerator,
            services.CaptureSourceFactory,
            services.ModelStore,
            Notify);

        Document = new SessionDocumentViewModel(services.Notes, Notify);
        Browser = new NotesBrowserViewModel(services.Notes, Notify);
        Browser.SessionActivated += OnSessionActivated;
        Capture.PropertyChanged += OnCapturePropertyChanged;

        _interimTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, OnInterimTick);
        _interimTimer.Start();
    }

    public CaptureViewModel Capture { get; }

    public SessionDocumentViewModel Document { get; }

    public NotesBrowserViewModel Browser { get; }

    public ObservableCollection<NotificationViewModel> Notifications { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyPropertyChangedFor(nameof(RecordButtonHint))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    public partial bool IsTransitioning { get; set; }

    [ObservableProperty] public partial string StatusMessage { get; set; } = "Ready.";

    public bool HasNotifications => Notifications.Count > 0;

    public string WindowTitle => "NoteScribe";

    public string NotesRootPath => _services.Notes.RootDirectory;

    public string SettingsPath => _services.Settings.SettingsPath;

    public string FfmpegStatusText => _services.Media.IsAvailable
        ? $"ffmpeg: {_services.Media.FfmpegPath}"
        : "ffmpeg: not found (video import unavailable)";

    public string RecordButtonText => IsTransitioning
        ? IsRecording ? "Stopping…" : "Starting…"
        : IsRecording ? "Stop" : "Start recording";

    public string RecordButtonHint => IsRecording
        ? "Finalises the session and renders notes.md"
        : "Creates a session and starts transcribing the selected channel";

    /// <summary>Loads settings, lists past sessions and starts pre-flight monitoring.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _settings = _services.Settings.Load();
        }
        catch (Exception ex)
        {
            _settings = new AppSettings();
            Notify("Could not read settings", $"{ex.Message} Falling back to defaults.", NotificationSeverity.Warning);
        }

        Capture.SelectChannel(_settings.LastChannelId);
        Capture.SelectModel(_settings.Model);
        Capture.Project = _settings.DefaultProject ?? string.Empty;
        _settingsLoaded = true;

        OnPropertyChanged(nameof(NotesRootPath));
        OnPropertyChanged(nameof(SettingsPath));
        OnPropertyChanged(nameof(FfmpegStatusText));

        if (_services.IsSampleData)
        {
            Notifications.Add(new NotificationViewModel(
                NotificationSeverity.Warning,
                "Sample data",
                "Core services are not wired yet, so devices, transcription and notes are simulated. Nothing here is a real recording.",
                Dismiss,
                canDismiss: false));
            OnPropertyChanged(nameof(HasNotifications));
        }

        await Browser.RefreshAsync().ConfigureAwait(true);
        await Capture.StartMonitoringAsync().ConfigureAwait(true);

        StatusMessage = "Ready.";
    }

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopRecordingAsync(faulted: false).ConfigureAwait(true);
        }
        else
        {
            await StartRecordingAsync().ConfigureAwait(true);
        }
    }

    private bool CanToggleRecording() => !IsTransitioning;

    private async Task StartRecordingAsync()
    {
        if (Capture.SelectedChannel is not { } channel)
        {
            Notify("Pick an audio channel", "Choose the endpoint Teams plays through before recording.", NotificationSeverity.Warning);
            return;
        }

        if (Capture.SelectedModel is not { } model)
        {
            Notify("Pick a model", "Choose which whisper weights to decode with.", NotificationSeverity.Warning);
            return;
        }

        if (!model.IsDownloaded)
        {
            Notify(
                $"{model.Name} is not downloaded",
                $"Download the weights ({model.SizeText}) before recording, or pick a model that is already on disk.",
                NotificationSeverity.Warning,
                actionLabel: "Download now",
                action: () => Capture.DownloadModelCommand.Execute(null));
            return;
        }

        IsTransitioning = true;
        try
        {
            _settings.LastChannelId = channel.Id;
            _settings.Model = model.Size;
            _settings.DefaultProject = string.IsNullOrWhiteSpace(Capture.Project) ? null : Capture.Project.Trim();
            await SaveSettingsAsync().ConfigureAwait(true);

            var title = string.IsNullOrWhiteSpace(Capture.SessionTitle)
                ? string.Create(CultureInfo.CurrentCulture, $"Meeting {DateTimeOffset.Now:yyyy-MM-dd HH:mm}")
                : Capture.SessionTitle.Trim();

            var sourceDescription = channel.IsLoopback
                ? $"Loopback: {channel.Name}"
                : $"Microphone: {channel.Name}";

            NoteSession session;
            try
            {
                session = await _services.Notes.CreateSessionAsync(
                    title,
                    _settings.DefaultProject,
                    sourceDescription,
                    [],
                    model.Name,
                    CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Notify("Could not create the session folder", ex.Message, NotificationSeverity.Error);
                return;
            }

            _liveSession = session;
            _pipelineFault = null;
            _lastSegmentAt = DateTimeOffset.Now;

            Document.BeginLiveSession(session);
            Browser.SelectedNode = null;

            await Capture.StopMonitoringAsync().ConfigureAwait(true);
            Capture.Meter.IsActive = true;
            Capture.BeginRecordingIndicator();
            IsRecording = true;
            StatusMessage = $"Recording “{session.Title}” from {channel.Name}.";

            _recordingCts = new CancellationTokenSource();
            var token = _recordingCts.Token;
            _recordingTask = Task.Run(() => RunPipelineAsync(channel.Channel, session, token), CancellationToken.None);
            _ = WatchPipelineAsync(_recordingTask);
        }
        finally
        {
            IsTransitioning = false;
        }
    }

    /// <summary>
    /// Capture -> engine -> disk -> UI. Runs entirely off the UI thread; only the final hand-off
    /// of each committed entry is marshalled back.
    /// </summary>
    private async Task RunPipelineAsync(AudioChannel channel, NoteSession session, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new MeteringCaptureSource(
                _services.CaptureSourceFactory.Create(channel),
                Capture.Meter.Report);

            var options = _settings.ToTranscriptionOptions();

            await foreach (var segment in _services.TranscriptionEngine
                               .RunAsync(source, options, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                var entry = new NoteEntry(
                    Guid.NewGuid().ToString("n"),
                    DateTimeOffset.Now,
                    segment.Start,
                    NoteEntryKind.Dictation,
                    segment.Text.Trim(),
                    Speaker: null,
                    Confidence: segment.Confidence);

                // Deliberately not the recording token: an utterance that has already been decoded
                // must reach disk even while we are shutting the session down.
                await _services.Notes.AppendEntryAsync(session.Id, entry, CancellationToken.None).ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    _lastSegmentAt = DateTimeOffset.Now;
                    Document.Append(entry);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            _pipelineFault = ex;
        }
    }

    private async Task WatchPipelineAsync(Task pipeline)
    {
        await pipeline.ConfigureAwait(true);

        if (_pipelineFault is { } fault && IsRecording && !_stopInProgress)
        {
            Notify(
                fault is AudioCaptureException ? "Audio device failed" : "Transcription stopped",
                $"{fault.Message} The session is being closed; everything captured so far is already saved.",
                NotificationSeverity.Error);

            await StopRecordingAsync(faulted: true).ConfigureAwait(true);
        }
    }

    private async Task StopRecordingAsync(bool faulted)
    {
        if (_stopInProgress || _liveSession is not { } session)
        {
            return;
        }

        _stopInProgress = true;
        IsTransitioning = true;
        StatusMessage = "Finalising session…";

        try
        {
            if (_recordingCts is not null)
            {
                await _recordingCts.CancelAsync().ConfigureAwait(true);
            }

            if (_recordingTask is not null)
            {
                await _recordingTask.ConfigureAwait(true);
            }

            _recordingCts?.Dispose();
            _recordingCts = null;
            _recordingTask = null;

            IsRecording = false;
            Capture.EndRecordingIndicator();
            Document.InterimText = null;

            try
            {
                var finalized = await _services.Notes
                    .FinalizeSessionAsync(session.Id, CancellationToken.None)
                    .ConfigureAwait(true);

                Document.MarkFinalized(finalized);

                var directory = _services.Notes.GetSessionDirectory(session.Id);
                StatusMessage = $"Saved to {Path.Combine(directory, "notes.md")}";
                Notify(
                    faulted ? "Session closed after an error" : "Session saved",
                    $"{Document.EntryCountText} written to {directory}",
                    faulted ? NotificationSeverity.Warning : NotificationSeverity.Info,
                    actionLabel: "Open folder",
                    action: () => OpenDirectory(directory));
            }
            catch (Exception ex)
            {
                Notify(
                    "Could not finalise the session",
                    $"{ex.Message} The raw transcript is still on disk and can be re-rendered.",
                    NotificationSeverity.Error);
            }

            _liveSession = null;
            await Browser.RefreshAsync().ConfigureAwait(true);
            await Capture.StartMonitoringAsync().ConfigureAwait(true);
        }
        finally
        {
            _stopInProgress = false;
            IsTransitioning = false;
        }
    }

    [RelayCommand]
    private void OpenNotesRoot() => OpenDirectory(_services.Notes.RootDirectory);

    private void OpenDirectory(string path)
    {
        try
        {
            SystemShell.OpenDirectory(path);
        }
        catch (Exception ex)
        {
            Notify("Could not open that folder", ex.Message, NotificationSeverity.Warning);
        }
    }

    private async void OnSessionActivated(object? sender, NoteSession session)
    {
        if (IsRecording)
        {
            Notify(
                "Still recording",
                "Stop the current session before opening an older one — the live notes stay on screen.",
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
            Notify("Could not open that session", ex.Message, NotificationSeverity.Error);
        }
    }

    /// <summary>
    /// The engine contract only yields committed segments, so the interim line reports pipeline
    /// state (audio arriving, nothing decoded yet) rather than inventing partial text.
    /// </summary>
    private void OnInterimTick(object? sender, EventArgs e)
    {
        if (!IsRecording)
        {
            Document.InterimText = null;
            return;
        }

        var since = DateTimeOffset.Now - _lastSegmentAt;

        Document.InterimText = (Capture.Meter.HasSignal, since.TotalSeconds) switch
        {
            (true, > 0.6) => string.Create(CultureInfo.CurrentCulture, $"decoding… {since.TotalSeconds:0.0}s of speech buffered"),
            (false, > 3.0) => "listening — no audio on this channel yet",
            _ => null
        };
    }

    private void OnCapturePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(CaptureViewModel.SelectedChannel):
                _settings.LastChannelId = Capture.SelectedChannel?.Id;
                QueueSettingsSave();
                break;
            case nameof(CaptureViewModel.SelectedModel):
                if (Capture.SelectedModel is { } model)
                {
                    _settings.Model = model.Size;
                    QueueSettingsSave();
                }

                break;
            case nameof(CaptureViewModel.Project):
                _settings.DefaultProject = string.IsNullOrWhiteSpace(Capture.Project) ? null : Capture.Project.Trim();
                QueueSettingsSave();
                break;
        }
    }

    private void QueueSettingsSave()
    {
        _settingsDebounce?.Cancel();
        _settingsDebounce?.Dispose();

        var cts = new CancellationTokenSource();
        _settingsDebounce = cts;
        _ = DebouncedSaveAsync(cts.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(600, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _services.Settings.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Notify("Could not save settings", ex.Message, NotificationSeverity.Warning);
        }
    }

    private void Notify(string title, string message, NotificationSeverity severity) =>
        Notify(title, message, severity, null, null);

    private void Notify(
        string title,
        string message,
        NotificationSeverity severity,
        string? actionLabel,
        Action? action)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Notify(title, message, severity, actionLabel, action));
            return;
        }

        var existing = Notifications.FirstOrDefault(n =>
            string.Equals(n.Title, title, StringComparison.Ordinal) && n.CanDismiss);

        if (existing is not null)
        {
            Notifications.Remove(existing);
        }

        Notifications.Add(new NotificationViewModel(severity, title, message, Dismiss, true, actionLabel, action));

        // Keep the banner stack short; the oldest dismissible one goes first.
        while (Notifications.Count > 4)
        {
            var oldest = Notifications.FirstOrDefault(n => n.CanDismiss);
            if (oldest is null)
            {
                break;
            }

            Notifications.Remove(oldest);
        }

        OnPropertyChanged(nameof(HasNotifications));
    }

    private void Dismiss(NotificationViewModel notification)
    {
        Notifications.Remove(notification);
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <summary>Finalises anything in flight so closing the window never loses a meeting.</summary>
    public async Task ShutdownAsync()
    {
        _interimTimer.Stop();

        if (IsRecording)
        {
            await StopRecordingAsync(faulted: false).ConfigureAwait(true);
        }

        await Capture.StopMonitoringAsync().ConfigureAwait(true);
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        _interimTimer.Stop();
        Browser.SessionActivated -= OnSessionActivated;
        Capture.PropertyChanged -= OnCapturePropertyChanged;
        _settingsDebounce?.Cancel();
        _settingsDebounce?.Dispose();
        _recordingCts?.Dispose();
        await Capture.DisposeAsync().ConfigureAwait(false);
    }
}

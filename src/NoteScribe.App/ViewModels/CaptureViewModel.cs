using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoteScribe.App.Services;
using NoteScribe.Core.Audio;
using NoteScribe.Core.Transcription;

namespace NoteScribe.App.ViewModels;

/// <summary>Everything in the top bar: what we listen to, what decodes it, and whether we are rolling.</summary>
public sealed partial class CaptureViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAudioChannelEnumerator _channels;
    private readonly IWhisperModelStore _modelStore;
    private readonly ChannelMonitor _monitor;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Action<string, string, NotificationSeverity> _notify;
    private DateTimeOffset _recordingStartedAt;
    private bool _suppressMonitorRestart;

    public CaptureViewModel(
        IAudioChannelEnumerator channels,
        IAudioCaptureSourceFactory captureSourceFactory,
        IWhisperModelStore modelStore,
        Action<string, string, NotificationSeverity> notify)
    {
        _channels = channels;
        _modelStore = modelStore;
        _notify = notify;
        _monitor = new ChannelMonitor(captureSourceFactory, Meter.Report, OnMonitorFailed);

        Models = [.. Enum.GetValues<WhisperModelSize>().Select(s => new ModelOptionViewModel(s))];
        _elapsedTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnElapsedTick);

        SessionTitle = DefaultTitle();
        RefreshModelState();
        LoadChannels(announceChanges: false);
    }

    public AudioMeterViewModel Meter { get; } = new();

    public ObservableCollection<ChannelOptionViewModel> Channels { get; } = [];

    public ObservableCollection<ModelOptionViewModel> Models { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelDetailText))]
    [NotifyPropertyChangedFor(nameof(HasChannel))]
    public partial ChannelOptionViewModel? SelectedChannel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    public partial ModelOptionViewModel? SelectedModel { get; set; }

    [ObservableProperty] public partial string SessionTitle { get; set; } = "";

    [ObservableProperty] public partial string Project { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingStateText))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty] public partial bool IsStarting { get; set; }

    [ObservableProperty] public partial string ElapsedText { get; set; } = "00:00:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty] public partial double DownloadFraction { get; set; }

    [ObservableProperty] public partial bool IsDownloadIndeterminate { get; set; }

    [ObservableProperty] public partial string DownloadStatusText { get; set; } = "";

    public bool HasChannel => SelectedChannel is not null;

    public string ChannelDetailText => SelectedChannel is { } channel
        ? $"{channel.KindLabel} · native {channel.FormatText} · resampled to 16 kHz mono"
        : "No endpoint selected — pick the device Teams plays through.";

    public string ModelStatusText => SelectedModel is { } model
        ? model.IsDownloaded ? $"{model.Name} · on disk" : $"{model.Name} · not downloaded ({model.SizeText})"
        : "no model selected";

    public string RecordingStateText => IsRecording ? "RECORDING" : "Idle";

    public bool ModelReady => SelectedModel?.IsDownloaded == true;

    /// <summary>Rebuilds the picker from the enumerator. Devices genuinely come and go.</summary>
    [RelayCommand]
    private void RefreshChannels() => LoadChannels(announceChanges: true);

    private void LoadChannels(bool announceChanges)
    {
        var previousId = SelectedChannel?.Id;

        IReadOnlyList<AudioChannel> live;
        try
        {
            live = _channels.GetChannels();
        }
        catch (Exception ex)
        {
            _notify("Could not list audio devices", ex.Message, NotificationSeverity.Error);
            return;
        }

        var ordered = live
            .OrderBy(c => c.Kind == AudioChannelKind.Loopback ? 0 : 1)
            .ThenByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _suppressMonitorRestart = true;
        try
        {
            Channels.Clear();
            AudioChannelKind? lastKind = null;
            foreach (var channel in ordered)
            {
                Channels.Add(new ChannelOptionViewModel(channel, showGroupHeader: lastKind != channel.Kind));
                lastKind = channel.Kind;
            }
        }
        finally
        {
            _suppressMonitorRestart = false;
        }

        var restored = previousId is null
            ? null
            : Channels.FirstOrDefault(c => string.Equals(c.Id, previousId, StringComparison.Ordinal));

        if (previousId is not null && restored is null && announceChanges)
        {
            _notify(
                "Selected audio device disappeared",
                "The endpoint you had chosen is no longer present. Pick another before recording.",
                NotificationSeverity.Warning);
        }

        SelectedChannel = restored ?? Channels.FirstOrDefault(c => c is { IsLoopback: true, IsDefault: true })
                                   ?? Channels.FirstOrDefault(c => c.IsLoopback)
                                   ?? Channels.FirstOrDefault();
    }

    /// <summary>Restores the persisted endpoint, falling back to the default loopback when it is gone.</summary>
    public void SelectChannel(string? channelId)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return;
        }

        var match = Channels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.Ordinal));
        if (match is not null)
        {
            SelectedChannel = match;
        }
    }

    public void SelectModel(WhisperModelSize size) =>
        SelectedModel = Models.FirstOrDefault(m => m.Size == size) ?? Models.FirstOrDefault();

    public void RefreshModelState()
    {
        foreach (var model in Models)
        {
            try
            {
                model.IsDownloaded = _modelStore.IsDownloaded(model.Size);
            }
            catch (Exception ex)
            {
                _notify("Could not inspect the model cache", ex.Message, NotificationSeverity.Warning);
                return;
            }
        }

        OnPropertyChanged(nameof(ModelStatusText));
        OnPropertyChanged(nameof(ModelReady));
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadModel), IncludeCancelCommand = true)]
    private async Task DownloadModelAsync(CancellationToken cancellationToken)
    {
        if (SelectedModel is not { } model)
        {
            return;
        }

        IsDownloading = true;
        DownloadFraction = 0;
        IsDownloadIndeterminate = true;
        DownloadStatusText = $"Starting download of {model.Name}…";

        // Progress<T> captures this (UI) context, so the handler is already on the right thread.
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            if (p.Fraction is { } fraction)
            {
                IsDownloadIndeterminate = false;
                DownloadFraction = fraction;
                DownloadStatusText = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{model.Name} · {p.BytesRead / 1024.0 / 1024.0:0} of {p.TotalBytes / 1024.0 / 1024.0:0} MB ({fraction:P0})");
            }
            else
            {
                IsDownloadIndeterminate = true;
                DownloadStatusText = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{model.Name} · {p.BytesRead / 1024.0 / 1024.0:0} MB downloaded");
            }
        });

        try
        {
            await _modelStore.EnsureDownloadedAsync(model.Size, progress, cancellationToken).ConfigureAwait(true);
            DownloadStatusText = $"{model.Name} ready.";
            _notify("Model downloaded", $"{model.Name} weights are cached and ready to use.", NotificationSeverity.Info);
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadStatusText = "Download failed.";
            _notify($"Could not download {model.Name}", ex.Message, NotificationSeverity.Error);
        }
        finally
        {
            IsDownloading = false;
            DownloadFraction = 0;
            RefreshModelState();
        }
    }

    private bool CanDownloadModel() => !IsDownloading && SelectedModel is { IsDownloaded: false };

    public async Task StartMonitoringAsync()
    {
        if (IsRecording || SelectedChannel is not { } channel)
        {
            return;
        }

        Meter.IsActive = true;
        await _monitor.StartAsync(channel.Channel).ConfigureAwait(true);
    }

    public async Task StopMonitoringAsync()
    {
        await _monitor.StopAsync().ConfigureAwait(true);
        Meter.IsActive = false;
        Meter.Reset();
    }

    public void BeginRecordingIndicator()
    {
        _recordingStartedAt = DateTimeOffset.Now;
        IsRecording = true;
        Meter.IsActive = true;
        ElapsedText = "00:00:00";
        _elapsedTimer.Start();
    }

    public void EndRecordingIndicator()
    {
        _elapsedTimer.Stop();
        IsRecording = false;
        SessionTitle = DefaultTitle();
    }

    private void OnElapsedTick(object? sender, EventArgs e) =>
        ElapsedText = (DateTimeOffset.Now - _recordingStartedAt).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    partial void OnSelectedChannelChanged(ChannelOptionViewModel? value)
    {
        if (_suppressMonitorRestart || value is null || IsRecording)
        {
            return;
        }

        _ = StartMonitoringAsync();
    }

    partial void OnSelectedModelChanged(ModelOptionViewModel? value)
    {
        OnPropertyChanged(nameof(ModelReady));
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    private void OnMonitorFailed(Exception ex) => Dispatcher.UIThread.Post(() =>
    {
        Meter.IsActive = false;
        _notify(
            "Cannot listen to that endpoint",
            $"{ex.Message} Pick a different channel, or refresh the list if the device was unplugged.",
            NotificationSeverity.Error);
    });

    private static string DefaultTitle() =>
        string.Create(CultureInfo.CurrentCulture, $"Meeting {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");

    public async ValueTask DisposeAsync()
    {
        _elapsedTimer.Stop();
        Meter.Dispose();
        await _monitor.DisposeAsync().ConfigureAwait(false);
    }
}

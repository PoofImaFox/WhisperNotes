using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Configuration;

namespace WhisperNotes.App.ViewModels;

/// <summary>
/// A configured input resolved to an endpoint that is currently present on this machine.
/// Capture orchestration uses this record rather than re-resolving settings itself.
/// </summary>
public sealed record ConfiguredAudioInput(string Id, string DisplayName, AudioChannel Channel);

/// <summary>
/// Configures the independent audio endpoints that should be transcribed together.
/// </summary>
/// <remarks>
/// Device enumeration is synchronous by contract. Settings writes are debounced, but only this
/// view model's fields are patched into a freshly loaded settings instance so unrelated settings
/// edited elsewhere in the shell cannot be reverted.
/// </remarks>
public sealed partial class InputSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(350);

    private readonly IAudioChannelEnumerator _channels;
    private readonly ISettingsStore _settings;
    private readonly Action<NotificationSeverity, string, string> _notify;
    private CancellationTokenSource? _saveDebounce;
    private bool _loading;

    public InputSettingsViewModel(
        IAudioChannelEnumerator channels,
        ISettingsStore settings,
        Action<NotificationSeverity, string, string> notify)
    {
        _channels = channels;
        _settings = settings;
        _notify = notify;
    }

    /// <summary>Raised immediately after the configured or enabled source set changes.</summary>
    public event EventHandler? InputsChanged;

    /// <summary>Raised after the current input configuration has been durably written.</summary>
    public event EventHandler? SettingsSaved;

    public ObservableCollection<InputSourceViewModel> Sources { get; } = [];

    public ObservableCollection<ChannelOptionViewModel> AvailableChannels { get; } = [];

    /// <summary>Enabled sources whose endpoint is currently connected.</summary>
    public IReadOnlyList<ConfiguredAudioInput> EnabledInputs =>
        Sources
            .Where(source => source.IsEnabled && source.SelectedChannel is not null)
            .Select(source => new ConfiguredAudioInput(
                source.Id,
                source.EffectiveDisplayName,
                source.SelectedChannel!.Channel))
            .ToList();

    /// <summary>Convenience projection for capture APIs that only need endpoints.</summary>
    public IReadOnlyList<AudioChannel> EnabledChannels =>
        EnabledInputs.Select(input => input.Channel).ToList();

    public int EnabledSourceCount => Sources.Count(source => source.IsEnabled);

    public int AvailableEnabledSourceCount =>
        Sources.Count(source => source.IsEnabled && source.SelectedChannel is not null);

    public int MissingEnabledSourceCount => EnabledSourceCount - AvailableEnabledSourceCount;

    public bool HasSources => Sources.Count > 0;

    public bool HasNoSources => !HasSources;

    public bool HasAvailableChannels => AvailableChannels.Count > 0;

    public bool HasMissingEnabledSources => MissingEnabledSourceCount > 0;

    public string SummaryText => EnabledSourceCount switch
    {
        0 => "No inputs enabled",
        1 when MissingEnabledSourceCount == 0 => "1 input enabled",
        1 => "1 input enabled · device unavailable",
        _ when MissingEnabledSourceCount == 0 => $"{EnabledSourceCount} inputs enabled for parallel transcription",
        _ => $"{EnabledSourceCount} inputs enabled · {MissingEnabledSourceCount} unavailable",
    };

    /// <summary>Loads persisted inputs, including the legacy LastChannelId fallback.</summary>
    public void Initialize()
    {
        AppSettings settings;
        try
        {
            settings = _settings.Load();
        }
        catch (Exception ex)
        {
            settings = new AppSettings();
            _notify(NotificationSeverity.Warning, "Could not read input settings", ex.Message);
        }

        _loading = true;
        try
        {
            RefreshAvailableChannels();
            Sources.Clear();

            IReadOnlyList<InputSourceSettings> persisted = settings.InputSources ?? [];
            if (persisted.Count == 0 && !string.IsNullOrWhiteSpace(settings.LastChannelId))
            {
                persisted =
                [
                    new InputSourceSettings
                    {
                        Id = "primary",
                        DisplayName = "Primary input",
                        ChannelId = settings.LastChannelId.Trim(),
                        Enabled = true,
                    },
                ];
            }

            foreach (InputSourceSettings source in persisted)
            {
                if (!string.IsNullOrWhiteSpace(source.ChannelId))
                {
                    Sources.Add(CreateSource(source));
                }
            }

            if (Sources.Count == 0 && AvailableChannels.FirstOrDefault() is { } defaultChannel)
            {
                Sources.Add(CreateSource(NewSource(defaultChannel)));
            }
        }
        finally
        {
            _loading = false;
        }

        PublishChanges(persist: true);
    }

    [RelayCommand]
    private void AddSource()
    {
        ChannelOptionViewModel? channel = AvailableChannels.FirstOrDefault(candidate =>
                                                   Sources.All(source =>
                                                       !string.Equals(
                                                           source.DeviceId,
                                                           candidate.Id,
                                                           StringComparison.Ordinal)))
                                               ?? AvailableChannels.FirstOrDefault();

        if (channel is null)
        {
            _notify(
                NotificationSeverity.Info,
                "No audio devices found",
                "Connect or enable a microphone or playback device, then refresh.");
            return;
        }

        Sources.Add(CreateSource(NewSource(channel)));
        PublishChanges(persist: true);
    }

    [RelayCommand]
    private void Refresh()
    {
        _loading = true;
        try
        {
            RefreshAvailableChannels();
            foreach (InputSourceViewModel source in Sources)
            {
                source.UpdateChannels(AvailableChannels);
            }

            if (Sources.Count == 0 && AvailableChannels.FirstOrDefault() is { } defaultChannel)
            {
                Sources.Add(CreateSource(NewSource(defaultChannel)));
            }
        }
        finally
        {
            _loading = false;
        }

        PublishChanges(persist: true);
    }

    private void RefreshAvailableChannels()
    {
        IReadOnlyList<AudioChannel> channels;
        try
        {
            channels = _channels.GetChannels();
        }
        catch (Exception ex)
        {
            channels = [];
            _notify(NotificationSeverity.Warning, "Could not list audio devices", ex.Message);
        }

        AvailableChannels.Clear();
        AudioChannelKind? previousKind = null;
        foreach (AudioChannel channel in channels)
        {
            bool showHeader = previousKind != channel.Kind;
            AvailableChannels.Add(new ChannelOptionViewModel(channel, showHeader));
            previousKind = channel.Kind;
        }

        OnPropertyChanged(nameof(HasAvailableChannels));
    }

    private InputSourceViewModel CreateSource(InputSourceSettings settings) =>
        new(settings, AvailableChannels, RemoveSource, OnSourceEdited);

    private InputSourceSettings NewSource(ChannelOptionViewModel channel)
    {
        string baseName = channel.IsMicrophone ? "Microphone" : "System audio";
        string name = UniqueName(baseName);
        return new InputSourceSettings
        {
            Id = Guid.NewGuid().ToString("n"),
            DisplayName = name,
            ChannelId = channel.Id,
            Kind = channel.Channel.Kind,
            Enabled = true,
        };
    }

    private string UniqueName(string baseName)
    {
        if (Sources.All(source =>
                !string.Equals(source.DisplayName, baseName, StringComparison.CurrentCultureIgnoreCase)))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (Sources.All(source =>
                    !string.Equals(source.DisplayName, candidate, StringComparison.CurrentCultureIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private void RemoveSource(InputSourceViewModel source)
    {
        if (Sources.Remove(source))
        {
            PublishChanges(persist: true);
        }
    }

    private void OnSourceEdited(InputSourceViewModel source)
    {
        if (!_loading)
        {
            PublishChanges(persist: true);
        }
    }

    private void PublishChanges(bool persist)
    {
        OnPropertyChanged(nameof(EnabledInputs));
        OnPropertyChanged(nameof(EnabledChannels));
        OnPropertyChanged(nameof(EnabledSourceCount));
        OnPropertyChanged(nameof(AvailableEnabledSourceCount));
        OnPropertyChanged(nameof(MissingEnabledSourceCount));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasNoSources));
        OnPropertyChanged(nameof(HasMissingEnabledSources));
        OnPropertyChanged(nameof(SummaryText));
        InputsChanged?.Invoke(this, EventArgs.Empty);

        if (persist)
        {
            QueueSave();
        }
    }

    private List<InputSourceSettings> Snapshot() =>
        Sources.Select(source => source.ToSettings()).ToList();

    private void QueueSave()
    {
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();

        var cts = new CancellationTokenSource();
        _saveDebounce = cts;
        _ = SaveAfterDelayAsync(Snapshot(), cts.Token);
    }

    private async Task SaveAfterDelayAsync(
        List<InputSourceSettings> snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDebounce, cancellationToken).ConfigureAwait(true);
            await SaveAsync(snapshot, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer edit superseded this snapshot, or the window is closing.
        }
    }

    /// <summary>Writes the current inputs immediately. Call from shell shutdown.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_saveDebounce is { } pending)
        {
            // CancelAsync, not Cancel: Cancel runs registered callbacks inline on the caller, which
            // here is the UI thread, and a slow one would stall the window as it closes.
            await pending.CancelAsync().ConfigureAwait(true);
        }

        await SaveAsync(Snapshot(), cancellationToken).ConfigureAwait(true);
    }

    private async Task SaveAsync(
        List<InputSourceSettings> snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            AppSettings current = _settings.Load();
            current.InputSources = snapshot.Select(source => source.Clone()).ToList();
            current.LastChannelId =
                snapshot.FirstOrDefault(source => source.Enabled)?.ChannelId
                ?? snapshot.FirstOrDefault()?.ChannelId;
            await _settings.SaveAsync(current, cancellationToken).ConfigureAwait(true);
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Closing or superseded.
        }
        catch (Exception ex)
        {
            _notify(NotificationSeverity.Warning, "Could not save input settings", ex.Message);
        }
    }
}

/// <summary>Editable row for one durable input configuration.</summary>
public sealed partial class InputSourceViewModel : ObservableObject
{
    private readonly ObservableCollection<ChannelOptionViewModel> _availableChannels;
    private readonly Action<InputSourceViewModel> _remove;
    private readonly Action<InputSourceViewModel> _changed;
    private string _channelId;
    private AudioChannelKind _kind;
    private bool _updating;

    public InputSourceViewModel(
        InputSourceSettings settings,
        ObservableCollection<ChannelOptionViewModel> availableChannels,
        Action<InputSourceViewModel> remove,
        Action<InputSourceViewModel> changed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _availableChannels = availableChannels;
        _remove = remove;
        _changed = changed;
        Id = string.IsNullOrWhiteSpace(settings.Id) ? Guid.NewGuid().ToString("n") : settings.Id;
        _channelId = settings.ChannelId;
        _kind = settings.Kind;
        DisplayName = settings.DisplayName;
        IsEnabled = settings.Enabled;
        UpdateChannels(availableChannels);
    }

    public string Id { get; }

    public IReadOnlyList<ChannelOptionViewModel> AvailableChannels => _availableChannels;

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    [ObservableProperty] public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty] public partial ChannelOptionViewModel? SelectedChannel { get; set; }

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? _kind == AudioChannelKind.Microphone ? "Microphone" : "System audio"
            : DisplayName.Trim();

    public string DeviceId => _channelId;

    public string KindLabel => _kind == AudioChannelKind.Microphone ? "MICROPHONE" : "LOOPBACK";

    public bool IsAvailable => SelectedChannel is not null;

    public bool IsMissing => !IsAvailable;

    public string AvailabilityText => IsAvailable
        ? SelectedChannel!.Detail
        : $"Device unavailable · {_channelId}";

    public void UpdateChannels(IReadOnlyList<ChannelOptionViewModel> channels)
    {
        _updating = true;
        try
        {
            SelectedChannel = channels.FirstOrDefault(channel =>
                string.Equals(channel.Id, _channelId, StringComparison.Ordinal));

            if (SelectedChannel is not null)
            {
                _kind = SelectedChannel.Channel.Kind;
            }
        }
        finally
        {
            _updating = false;
        }

        NotifyDerivedProperties();
    }

    public InputSourceSettings ToSettings() => new()
    {
        Id = Id,
        DisplayName = EffectiveDisplayName,
        ChannelId = _channelId,
        Kind = _kind,
        Enabled = IsEnabled,
    };

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnIsEnabledChanged(bool value) => Changed();

    partial void OnDisplayNameChanged(string value) => Changed();

    partial void OnSelectedChannelChanged(ChannelOptionViewModel? value)
    {
        if (value is not null)
        {
            _channelId = value.Id;
            _kind = value.Channel.Kind;
        }

        NotifyDerivedProperties();
        Changed();
    }

    private void Changed()
    {
        if (!_updating)
        {
            _changed(this);
        }
    }

    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(EffectiveDisplayName));
        OnPropertyChanged(nameof(DeviceId));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(AvailabilityText));
    }
}

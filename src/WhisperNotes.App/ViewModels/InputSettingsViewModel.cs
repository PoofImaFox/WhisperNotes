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

    /// <summary>True when an input targets a single application but this OS cannot capture one.</summary>
    /// <remarks>
    /// Deliberately not an error state. The capture factory falls back to device-level loopback, so
    /// the recording still happens — it just also contains everything else playing on the machine.
    /// That is a thing to learn before the meeting, not while reading the transcript afterwards, so
    /// it is surfaced the moment such an input exists rather than at record time.
    /// </remarks>
    public bool HasProcessLoopbackWarning =>
        !ProcessLoopbackSupport.IsSupported && Sources.Any(source => source.IsApplication);

    /// <summary>The OS's own reason, so the copy cannot drift from the check that produced it.</summary>
    public string ProcessLoopbackWarningText => ProcessLoopbackSupport.UnsupportedReason ?? string.Empty;

    public string SummaryText => EnabledSourceCount switch
    {
        0 => "No inputs enabled",
        1 when MissingEnabledSourceCount == 0 => "1 input enabled",
        // "input", not "device": the one that is missing may be an application that has since quit.
        1 => "1 input enabled · unavailable",
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
        // Endpoints before applications. An application channel only exists while that app happens
        // to be running, so seeding a brand-new input with one would hand the user a row that is
        // already "not running" the next time they open the page; a device is still there tomorrow.
        // With no application channels present this is exactly the previous first-unused behaviour.
        ChannelOptionViewModel? channel = FirstUnusedChannel(applicationsAllowed: false)
                                          ?? FirstUnusedChannel(applicationsAllowed: true)
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

    private ChannelOptionViewModel? FirstUnusedChannel(bool applicationsAllowed) =>
        AvailableChannels.FirstOrDefault(candidate =>
            (applicationsAllowed || !candidate.IsApplication)
            && Sources.All(source =>
                !string.Equals(source.DeviceId, candidate.Id, StringComparison.Ordinal)));

    /// <summary>Re-enumerates endpoints and running applications.</summary>
    /// <remarks>
    /// This matters far more now than it did with hardware alone: an application channel appears
    /// when the app starts and disappears when it quits, so the picker is stale the moment the user
    /// opens Teams. Re-resolution is by <see cref="AudioChannel.Id"/>, and application ids are keyed
    /// on the executable rather than the pid, so a source survives the app being restarted. An app
    /// that is genuinely gone resolves to nothing and the row falls into the same "unavailable"
    /// state a disconnected microphone does — it must not silently revert to another channel.
    /// </remarks>
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

    internal static string DefaultNameFor(ChannelOptionViewModel channel) => channel.Channel.Kind switch
    {
        AudioChannelKind.Microphone => "Microphone",
        // An application's friendly name ("Microsoft Teams") is already the label the user would
        // have typed, and calling it "System audio" would misdescribe what the input records.
        AudioChannelKind.Application when !string.IsNullOrWhiteSpace(channel.Name) => channel.Name,
        AudioChannelKind.Application => "Application",
        _ => "System audio",
    };

    private InputSourceSettings NewSource(ChannelOptionViewModel channel)
    {
        string name = UniqueName(DefaultNameFor(channel));
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
        OnPropertyChanged(nameof(HasProcessLoopbackWarning));
        OnPropertyChanged(nameof(ProcessLoopbackWarningText));
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
    /// <summary>Names we generated ourselves, and may therefore replace when the channel changes.</summary>
    private static readonly string[] GenericNames = ["System audio", "Microphone", "Application", "Input"];

    private string _channelId;
    private AudioChannelKind _kind;
    private bool _updating;

    /// <summary>False while the display name is still one we generated, so it may track the channel.</summary>
    private bool _nameIsCustom;

    /// <summary>Guards the rename we perform ourselves from being mistaken for the user typing.</summary>
    private bool _renamingFromChannel;

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

        // A name we generated is a description of the channel, so it should follow the channel. A name
        // the user typed is theirs, and we must never overwrite it. We cannot tell them apart after the
        // fact, so classify once on load: anything matching a generated name is treated as ours.
        _nameIsCustom = !IsGeneratedName(settings.DisplayName);

        UpdateChannels(availableChannels);
    }

    /// <summary>
    /// True when <paramref name="name"/> looks like one this view model produced rather than one the
    /// user typed — either a generic kind label or a channel's own friendly name, with or without the
    /// " 2"/" 3" suffix <see cref="InputSettingsViewModel.UniqueName"/> appends to break ties.
    /// </summary>
    private bool IsGeneratedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        string trimmed = StripDuplicateSuffix(name.Trim());

        return GenericNames.Contains(trimmed, StringComparer.CurrentCultureIgnoreCase)
            || _availableChannels.Any(channel => string.Equals(
                trimmed,
                InputSettingsViewModel.DefaultNameFor(channel),
                StringComparison.CurrentCultureIgnoreCase));
    }

    private static string StripDuplicateSuffix(string name)
    {
        int space = name.LastIndexOf(' ');
        return space > 0 && int.TryParse(name.AsSpan(space + 1), out _) ? name[..space] : name;
    }

    public string Id { get; }

    public IReadOnlyList<ChannelOptionViewModel> AvailableChannels => _availableChannels;

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    [ObservableProperty] public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty] public partial ChannelOptionViewModel? SelectedChannel { get; set; }

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? FallbackDisplayName : DisplayName.Trim();

    public string DeviceId => _channelId;

    /// <summary>True when this input targets one application rather than an endpoint.</summary>
    /// <remarks>
    /// Read from the persisted kind, not from <see cref="SelectedChannel"/>, so an input whose app
    /// has since quit is still known to be an application input — that is precisely when the page
    /// most needs to explain itself.
    /// </remarks>
    public bool IsApplication => _kind == AudioChannelKind.Application;

    public string KindLabel => _kind switch
    {
        AudioChannelKind.Microphone => "MICROPHONE",
        AudioChannelKind.Application => "APPLICATION",
        _ => "LOOPBACK",
    };

    public bool IsAvailable => SelectedChannel is not null;

    public bool IsMissing => !IsAvailable;

    /// <summary>
    /// An application that is not running is not a broken device — saying "device unavailable"
    /// would send the user hunting through Sound settings for a cable that was never unplugged.
    /// </summary>
    public string AvailabilityText => IsAvailable
        ? SelectedChannel!.Detail
        : IsApplication
            ? $"Not running · {ExecutableName ?? _channelId}"
            : $"Device unavailable · {_channelId}";

    private string? ExecutableName => ApplicationChannelId.ExecutableOf(_channelId);

    private string FallbackDisplayName => _kind switch
    {
        AudioChannelKind.Microphone => "Microphone",
        AudioChannelKind.Application => ExecutableName ?? "Application",
        _ => "System audio",
    };

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

    partial void OnDisplayNameChanged(string value)
    {
        if (!_renamingFromChannel)
        {
            _nameIsCustom = true;
        }

        Changed();
    }

    partial void OnSelectedChannelChanged(ChannelOptionViewModel? value)
    {
        if (value is not null)
        {
            _channelId = value.Id;
            _kind = value.Channel.Kind;

            // Otherwise switching a "System audio" input to Discord leaves it labelled "System audio",
            // and the note it writes claims to be something it is not.
            if (!_nameIsCustom)
            {
                _renamingFromChannel = true;
                try
                {
                    DisplayName = InputSettingsViewModel.DefaultNameFor(value);
                }
                finally
                {
                    _renamingFromChannel = false;
                }
            }
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
        OnPropertyChanged(nameof(IsApplication));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(AvailabilityText));
    }
}

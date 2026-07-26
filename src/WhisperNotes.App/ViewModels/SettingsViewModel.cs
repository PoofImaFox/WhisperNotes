using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperNotes.Core.Diarization;

namespace WhisperNotes.App.ViewModels;

/// <summary>Settings-page state that is not owned by one of the feature view models.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISpeakerProfileStore _speakerProfiles;
    private readonly Action<string, string, NotificationSeverity> _notify;

    public SettingsViewModel(
        AiSettingsViewModel ai,
        ISpeakerProfileStore speakerProfiles,
        Action<string, string, NotificationSeverity> notify)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(speakerProfiles);
        ArgumentNullException.ThrowIfNull(notify);

        Ai = ai;
        _speakerProfiles = speakerProfiles;
        _notify = notify;

        var assembly = typeof(SettingsViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        VersionText = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString(3) ?? "development build"
            : informationalVersion.Split('+', 2)[0];

        RefreshSpeakerProfiles();
    }

    /// <summary>The same instance used by the note editor, so provider changes take effect immediately.</summary>
    public AiSettingsViewModel Ai { get; }

    public ObservableCollection<SpeakerProfileItemViewModel> SpeakerProfiles { get; } = [];

    public string SpeakerProfilesPath => _speakerProfiles.ProfilesPath;

    public bool HasSpeakerProfiles => SpeakerProfiles.Count > 0;

    public bool HasNoSpeakerProfiles => !HasSpeakerProfiles;

    public string SpeakerProfileCountText => SpeakerProfiles.Count switch
    {
        0 => "No voice profiles saved",
        1 => "1 voice profile saved",
        var count => string.Create(CultureInfo.CurrentCulture, $"{count} voice profiles saved"),
    };

    public string VersionText { get; }

    public string RuntimeText => RuntimeInformation.FrameworkDescription;

    public string PlatformText => RuntimeInformation.OSDescription;

    [RelayCommand]
    private void RefreshSpeakerProfiles()
    {
        SpeakerProfiles.Clear();
        foreach (SpeakerVoiceProfile profile in _speakerProfiles.Load())
        {
            SpeakerProfiles.Add(new SpeakerProfileItemViewModel(profile, _speakerProfiles, _notify));
        }

        OnPropertyChanged(nameof(HasSpeakerProfiles));
        OnPropertyChanged(nameof(HasNoSpeakerProfiles));
        OnPropertyChanged(nameof(SpeakerProfileCountText));
    }
}

/// <summary>One independently matched voiceprint shown in the speaker-identification tab.</summary>
public sealed partial class SpeakerProfileItemViewModel : ObservableObject
{
    private readonly ISpeakerProfileStore _store;
    private readonly Action<string, string, NotificationSeverity> _notify;
    private string _savedName;

    public SpeakerProfileItemViewModel(
        SpeakerVoiceProfile profile,
        ISpeakerProfileStore store,
        Action<string, string, NotificationSeverity> notify)
    {
        ProfileId = profile.Id;
        _store = store;
        _notify = notify;
        _savedName = profile.Name ?? string.Empty;
        Name = _savedName;
        CreatedText = profile.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        DimensionsText = string.Create(
            CultureInfo.CurrentCulture,
            $"{profile.VoicePrint.Length} dimensions");
    }

    public string ProfileId { get; }

    public string ShortId => ProfileId.Length <= 8 ? ProfileId : ProfileId[..8];

    public string CreatedText { get; }

    public string DimensionsText { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNameCommand))]
    public partial string Name { get; set; } = string.Empty;

    private bool CanSaveName() =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.Equals(Name.Trim(), _savedName, StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanSaveName))]
    private async Task SaveNameAsync()
    {
        string name = Name.Trim();
        try
        {
            SpeakerVoiceProfile renamed = await _store
                .RenameAsync(ProfileId, name, CancellationToken.None)
                .ConfigureAwait(true);
            _savedName = renamed.Name ?? string.Empty;
            Name = _savedName;
        }
        catch (Exception ex)
        {
            _notify("Could not rename that voice profile", ex.Message, NotificationSeverity.Error);
        }

        SaveNameCommand.NotifyCanExecuteChanged();
    }
}

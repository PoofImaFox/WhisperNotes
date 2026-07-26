using WhisperNotes.App.ViewModels;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Configuration;

namespace WhisperNotes.App.DesignData;

/// <summary>Isolated designer entry point so the input page can be previewed before shell wiring.</summary>
public static class InputSettingsDesignData
{
    public static InputSettingsViewModel ViewModel { get; } = Build();

    private static InputSettingsViewModel Build()
    {
        var settings = new FakeSettingsStore();
        Seed(settings);

        var viewModel = new InputSettingsViewModel(
            new FakeAudioChannelEnumerator(),
            settings,
            static (_, _, _) => { });
        viewModel.Initialize();
        return viewModel;
    }

    /// <summary>
    /// Four inputs — loopback, microphone and two applications — rather than the single legacy
    /// input the empty store would produce.
    /// </summary>
    /// <remarks>
    /// Two application rows are the point: the previewer has to show the case the feature exists
    /// for, which is Teams and a browser transcribed side by side, and it is also the only way the
    /// process-loopback capability notice is ever laid out at design time.
    /// </remarks>
    private static void Seed(FakeSettingsStore store)
    {
        AppSettings settings = store.Load();
        settings.InputSources =
        [
            Source("design-loopback", "System audio", DefaultOf(AudioChannelKind.Loopback), AudioChannelKind.Loopback),
            Source("design-microphone", "My microphone", DefaultOf(AudioChannelKind.Microphone), AudioChannelKind.Microphone),
            Source("design-teams", "Microsoft Teams", ApplicationChannelId.ForExecutable("ms-teams.exe"), AudioChannelKind.Application),
            Source("design-chrome", "Google Chrome", ApplicationChannelId.ForExecutable("chrome.exe"), AudioChannelKind.Application),
        ];

        // Written back explicitly rather than trusting Load() to have handed out the store's own
        // instance. The fake completes synchronously, so blocking here cannot deadlock the designer.
        store.SaveAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string DefaultOf(AudioChannelKind kind) =>
        SampleData.Channels.First(channel => channel.Kind == kind && channel.IsDefault).Id;

    private static InputSourceSettings Source(
        string id,
        string displayName,
        string channelId,
        AudioChannelKind kind) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            ChannelId = channelId,
            Kind = kind,
            Enabled = true,
        };
}

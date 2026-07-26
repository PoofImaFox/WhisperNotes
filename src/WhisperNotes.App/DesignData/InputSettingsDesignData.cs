using WhisperNotes.App.ViewModels;

namespace WhisperNotes.App.DesignData;

/// <summary>Isolated designer entry point so the input page can be previewed before shell wiring.</summary>
public static class InputSettingsDesignData
{
    public static InputSettingsViewModel ViewModel { get; } = Build();

    private static InputSettingsViewModel Build()
    {
        var viewModel = new InputSettingsViewModel(
            new FakeAudioChannelEnumerator(),
            new FakeSettingsStore(),
            static (_, _, _) => { });
        viewModel.Initialize();
        return viewModel;
    }
}

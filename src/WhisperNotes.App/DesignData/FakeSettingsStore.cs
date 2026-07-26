using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.DesignData;

/// <summary>Keeps settings in memory for the lifetime of the process.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private AppSettings _settings = new()
    {
        NotesRoot = Path.Combine(Path.GetTempPath(), "WhisperNotes-SampleNotes"),
        ModelsRoot = Path.Combine(Path.GetTempPath(), "WhisperNotes-SampleModels"),
        Model = WhisperModelSize.Base,
        DefaultProject = "Northwind Logistics",
        LastChannelId = SampleData.Channels[0].Id,
        InitialPrompt = "Northwind Logistics, Halcyon Care Group, Veeam, Always On, VLAN, E3",
    };

    public string SettingsPath { get; } =
        Path.Combine(Path.GetTempPath(), "WhisperNotes-SampleNotes", "settings.json");

    public AppSettings Load() => _settings;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}

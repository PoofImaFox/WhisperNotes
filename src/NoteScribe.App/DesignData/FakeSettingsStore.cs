using NoteScribe.Core.Configuration;
using NoteScribe.Core.Transcription;

namespace NoteScribe.App.DesignData;

/// <summary>Keeps settings in memory for the lifetime of the process.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private AppSettings _settings = new()
    {
        NotesRoot = Path.Combine(Path.GetTempPath(), "NoteScribe-SampleNotes"),
        ModelsRoot = Path.Combine(Path.GetTempPath(), "NoteScribe-SampleModels"),
        Model = WhisperModelSize.Base,
        DefaultProject = "Northwind Logistics",
        LastChannelId = SampleData.Channels[0].Id,
        InitialPrompt = "Northwind Logistics, Halcyon Care Group, Veeam, Always On, VLAN, E3",
    };

    public string SettingsPath { get; } =
        Path.Combine(Path.GetTempPath(), "NoteScribe-SampleNotes", "settings.json");

    public AppSettings Load() => _settings;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}

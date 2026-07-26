using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Tests.Configuration;

/// <summary>
/// GPU decode is worth roughly 40x on large-v3-turbo, so the thing worth pinning is that it stays
/// on by itself: through a fresh install, through a settings file written before the setting
/// existed, and through a damaged one.
/// </summary>
public sealed class GpuSettingsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"whispernotes-gpu-{Guid.NewGuid():n}");

    [Fact]
    public void ToTranscriptionOptions_CarriesTheGpuSettingsThrough()
    {
        AppSettings settings = new() { Gpu = { Enabled = false, Device = 2 } };

        TranscriptionOptions options = settings.ToTranscriptionOptions();

        Assert.False(options.UseGpu);
        Assert.Equal(2, options.GpuDevice);
    }

    [Fact]
    public void ToTranscriptionOptions_DefaultsToTheFirstDeviceWithTheGpuOn()
    {
        TranscriptionOptions options = new AppSettings().ToTranscriptionOptions();

        Assert.True(options.UseGpu);
        Assert.Equal(0, options.GpuDevice);
    }

    [Fact]
    public void Load_LeavesTheGpuOnForASettingsFileWrittenBeforeTheSettingExisted()
    {
        AppSettings settings = LoadFrom("""{"model":4,"language":"en"}""");

        Assert.True(settings.Gpu.Enabled);
        Assert.Equal(0, settings.Gpu.Device);
    }

    [Fact]
    public void Load_SurvivesANullGpuNode()
    {
        AppSettings settings = LoadFrom("""{"gpu":null}""");

        Assert.True(settings.Gpu.Enabled);
    }

    [Fact]
    public void Load_KeepsAnExplicitlyDisabledGpuDisabled()
    {
        AppSettings settings = LoadFrom("""{"gpu":{"enabled":false,"device":1}}""");

        Assert.False(settings.Gpu.Enabled);
        Assert.Equal(1, settings.Gpu.Device);
    }

    [Fact]
    public void Load_FallsBackToTheFirstDeviceWhenTheIndexAddressesNothing()
    {
        AppSettings settings = LoadFrom("""{"gpu":{"enabled":true,"device":-3}}""");

        Assert.True(settings.Gpu.Enabled);
        Assert.Equal(0, settings.Gpu.Device);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheGpuSettings()
    {
        string path = SettingsPath();
        JsonSettingsStore store = new(path);

        await store.SaveAsync(new AppSettings { Gpu = { Enabled = false, Device = 3 } }, CancellationToken.None);
        AppSettings reloaded = store.Load();

        Assert.False(reloaded.Gpu.Enabled);
        Assert.Equal(3, reloaded.Gpu.Device);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AppSettings LoadFrom(string json)
    {
        string path = SettingsPath();
        File.WriteAllText(path, json);
        return new JsonSettingsStore(path).Load();
    }

    private string SettingsPath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "settings.json");
    }
}

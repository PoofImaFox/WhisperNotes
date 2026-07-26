using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Configuration;

namespace WhisperNotes.Core.Tests.Configuration;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"whispernotes-settings-{Guid.NewGuid():n}");

    [Fact]
    public void Load_MigratesLegacyLastChannelToOneEnabledInput()
    {
        string path = SettingsPath();
        File.WriteAllText(path, """{"lastChannelId":"legacy-endpoint"}""");

        AppSettings settings = new JsonSettingsStore(path).Load();

        InputSourceSettings input = Assert.Single(settings.InputSources);
        Assert.Equal("primary", input.Id);
        Assert.Equal("legacy-endpoint", input.ChannelId);
        Assert.Equal("Primary input", input.DisplayName);
        Assert.True(input.Enabled);
        Assert.Equal("legacy-endpoint", settings.LastChannelId);
    }

    [Fact]
    public void Load_NormalisesInputRowsAndMirrorsTheFirstEnabledChannel()
    {
        string path = SettingsPath();
        File.WriteAllText(
            path,
            """
            {
              "lastChannelId": "old-endpoint",
              "inputSources": [
                {
                  "id": "room",
                  "displayName": "  ",
                  "channelId": " microphone-id ",
                  "kind": 1,
                  "enabled": true
                },
                {
                  "id": "room",
                  "displayName": "Playback",
                  "channelId": "loopback-id",
                  "kind": 0,
                  "enabled": false
                },
                {
                  "id": "ignored",
                  "displayName": "No device",
                  "channelId": "",
                  "enabled": true
                }
              ]
            }
            """);

        AppSettings settings = new JsonSettingsStore(path).Load();

        Assert.Collection(
            settings.InputSources,
            microphone =>
            {
                Assert.Equal("room", microphone.Id);
                Assert.Equal("Microphone", microphone.DisplayName);
                Assert.Equal("microphone-id", microphone.ChannelId);
                Assert.Equal(AudioChannelKind.Microphone, microphone.Kind);
                Assert.True(microphone.Enabled);
            },
            loopback =>
            {
                Assert.Equal("room-2", loopback.Id);
                Assert.Equal("Playback", loopback.DisplayName);
                Assert.Equal("loopback-id", loopback.ChannelId);
                Assert.Equal(AudioChannelKind.Loopback, loopback.Kind);
                Assert.False(loopback.Enabled);
            });
        Assert.Equal("microphone-id", settings.LastChannelId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SettingsPath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "settings.json");
    }
}

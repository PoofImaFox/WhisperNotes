using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Configuration;

namespace WhisperNotes.Core.Tests.Audio;

/// <summary>
/// A per-application input is only worth configuring if it is still pointing at the same app
/// tomorrow. The id is keyed on the executable exactly so it outlives the process it was captured
/// from, so what needs pinning is the trip through settings.json — including the normalisation pass
/// that rewrites every input row on load and must leave the <c>app:</c> scheme alone.
/// </summary>
public sealed class ApplicationInputSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"whispernotes-app-input-{Guid.NewGuid():n}");

    [Fact]
    public async Task SaveThenLoad_RoundTripsAnApplicationInput()
    {
        string path = SettingsPath();
        JsonSettingsStore store = new(path);
        AppSettings settings = new()
        {
            InputSources =
            [
                new InputSourceSettings
                {
                    Id = "teams",
                    DisplayName = "Teams",
                    ChannelId = ApplicationChannelId.ForExecutable("teams.exe"),
                    Kind = AudioChannelKind.Application,
                    Enabled = true,
                },
            ],
        };

        await store.SaveAsync(settings, CancellationToken.None);
        AppSettings reloaded = store.Load();

        InputSourceSettings input = Assert.Single(reloaded.InputSources);
        Assert.Equal("teams", input.Id);
        Assert.Equal("Teams", input.DisplayName);
        Assert.Equal("app:teams.exe", input.ChannelId);
        Assert.Equal(AudioChannelKind.Application, input.Kind);
        Assert.True(input.Enabled);

        // The legacy mirror has to follow too, or an older build reopens on the wrong endpoint.
        Assert.Equal("app:teams.exe", reloaded.LastChannelId);
    }

    /// <summary>
    /// The persisted form is the number, not the member name, so this is the test that would catch a
    /// reordered enum from the settings side.
    /// </summary>
    [Fact]
    public void Load_ReadsTheApplicationKindFromItsPersistedNumber()
    {
        AppSettings settings = LoadFrom(
            """
            {
              "inputSources": [
                {
                  "id": "teams",
                  "displayName": "Teams",
                  "channelId": "app:teams.exe",
                  "kind": 2,
                  "enabled": true
                }
              ]
            }
            """);

        InputSourceSettings input = Assert.Single(settings.InputSources);
        Assert.Equal(AudioChannelKind.Application, input.Kind);
        Assert.Equal("app:teams.exe", input.ChannelId);
    }

    /// <summary>
    /// Normalisation trims and nothing more: re-casing or re-prefixing a hand-edited id would point
    /// the input at an application that does not exist under that name.
    /// </summary>
    [Fact]
    public void Load_TrimsAnApplicationIdWithoutOtherwiseRewritingIt()
    {
        AppSettings settings = LoadFrom(
            """
            {
              "inputSources": [
                {
                  "id": "teams",
                  "displayName": "Teams",
                  "channelId": "  app:MS-Teams.exe  ",
                  "kind": 2,
                  "enabled": true
                }
              ]
            }
            """);

        InputSourceSettings input = Assert.Single(settings.InputSources);
        Assert.Equal("app:MS-Teams.exe", input.ChannelId);
        Assert.Equal(AudioChannelKind.Application, input.Kind);
    }

    /// <summary>
    /// Application and device inputs are captured side by side, so the pass that de-duplicates ids
    /// and mirrors the primary channel has to treat them as ordinary rows.
    /// </summary>
    [Fact]
    public void Load_KeepsAnApplicationInputAlongsideADeviceInput()
    {
        AppSettings settings = LoadFrom(
            """
            {
              "inputSources": [
                {
                  "id": "primary",
                  "displayName": "Headset",
                  "channelId": "{0.0.1.00000000}.{b7e6c3a1-5d24-4f81-9c0e-2a6f8d1b4c37}",
                  "kind": 1,
                  "enabled": true
                },
                {
                  "id": "primary",
                  "displayName": "Teams",
                  "channelId": "app:teams.exe",
                  "kind": 2,
                  "enabled": true
                }
              ]
            }
            """);

        Assert.Collection(
            settings.InputSources,
            microphone =>
            {
                Assert.Equal("primary", microphone.Id);
                Assert.Equal(AudioChannelKind.Microphone, microphone.Kind);
            },
            application =>
            {
                Assert.Equal("primary-2", application.Id);
                Assert.Equal("Teams", application.DisplayName);
                Assert.Equal("app:teams.exe", application.ChannelId);
                Assert.Equal(AudioChannelKind.Application, application.Kind);
            });
    }

    [Fact]
    public void Clone_CarriesTheApplicationKindAndId()
    {
        InputSourceSettings original = new()
        {
            Id = "teams",
            DisplayName = "Teams",
            ChannelId = ApplicationChannelId.ForExecutable("teams.exe"),
            Kind = AudioChannelKind.Application,
            Enabled = false,
        };

        InputSourceSettings copy = original.Clone();

        Assert.NotSame(original, copy);
        Assert.Equal("teams", copy.Id);
        Assert.Equal("Teams", copy.DisplayName);
        Assert.Equal("app:teams.exe", copy.ChannelId);
        Assert.Equal(AudioChannelKind.Application, copy.Kind);
        Assert.False(copy.Enabled);
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

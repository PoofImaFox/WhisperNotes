using WhisperNotes.Core.Audio;

namespace WhisperNotes.App.DesignData;

/// <summary>
/// Stands in for the WASAPI enumerator. Shuffles one entry in and out on every other refresh so
/// the "devices come and go" path in the UI is exercised without real hardware.
/// </summary>
/// <remarks>
/// Applications churn much faster than endpoints — a monitor is unplugged once a month, a browser
/// is closed every hour — so Chrome is cycled on a shorter period than the DELL display. That is
/// what walks the previewer through the state the real feature spends most of its time in: a
/// configured input whose application is no longer running.
/// </remarks>
internal sealed class FakeAudioChannelEnumerator : IAudioChannelEnumerator
{
    /// <summary>
    /// Plausible per-application channels. Ids are built with <see cref="ApplicationChannelId"/>
    /// rather than written out, so the fake cannot drift from the format Core persists.
    /// </summary>
    private static readonly IReadOnlyList<AudioChannel> Applications =
    [
        new(ApplicationChannelId.ForExecutable("ms-teams.exe"), "Microsoft Teams",
            AudioChannelKind.Application, false, 48_000, 2, 8124, "ms-teams.exe"),
        new(ApplicationChannelId.ForExecutable("chrome.exe"), "Google Chrome",
            AudioChannelKind.Application, false, 48_000, 2, 13776, "chrome.exe"),
    ];

    private static readonly IReadOnlyList<AudioChannel> AllChannels =
        [.. SampleData.Channels, .. Applications];

    private int _refreshCount;

    public IReadOnlyList<AudioChannel> GetChannels()
    {
        var refresh = Interlocked.Increment(ref _refreshCount);
        var transient = refresh % 3 != 0;
        var chromeRunning = refresh % 2 != 0;

        // Ordered by kind so applications land last, matching the real enumerator's contract — the
        // picker's group headers are computed from adjacency and would fragment otherwise.
        return AllChannels
            .Where(c => transient || !c.Name.StartsWith("DELL", StringComparison.Ordinal))
            .Where(c => chromeRunning
                        || !string.Equals(c.ExecutableName, "chrome.exe", StringComparison.Ordinal))
            .OrderBy(c => c.Kind)
            .ThenByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public AudioChannel? Find(string channelId) =>
        AllChannels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.Ordinal));
}

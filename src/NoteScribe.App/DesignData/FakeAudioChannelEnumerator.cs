using NoteScribe.Core.Audio;

namespace NoteScribe.App.DesignData;

/// <summary>
/// Stands in for the WASAPI enumerator. Shuffles one entry in and out on every other refresh so
/// the "devices come and go" path in the UI is exercised without real hardware.
/// </summary>
internal sealed class FakeAudioChannelEnumerator : IAudioChannelEnumerator
{
    private int _refreshCount;

    public IReadOnlyList<AudioChannel> GetChannels()
    {
        var transient = Interlocked.Increment(ref _refreshCount) % 3 != 0;

        return SampleData.Channels
            .Where(c => transient || !c.Name.StartsWith("DELL", StringComparison.Ordinal))
            .OrderBy(c => c.Kind)
            .ThenByDescending(c => c.IsDefault)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public AudioChannel? Find(string channelId) =>
        SampleData.Channels.FirstOrDefault(c => string.Equals(c.Id, channelId, StringComparison.Ordinal));
}

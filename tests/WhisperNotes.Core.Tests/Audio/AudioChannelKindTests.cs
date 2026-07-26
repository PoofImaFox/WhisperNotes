using WhisperNotes.Core.Audio;

namespace WhisperNotes.Core.Tests.Audio;

/// <summary>
/// <see cref="AudioChannelKind"/> is persisted by number in settings.json, which makes those numbers
/// part of the file format rather than an implementation detail. Adding <c>Application</c> anywhere
/// but the end would have turned every saved microphone into a loopback on the next load, silently,
/// on machines that had never asked for per-application capture at all.
/// </summary>
public sealed class AudioChannelKindTests
{
    [Theory]
    [InlineData(AudioChannelKind.Loopback, 0)]
    [InlineData(AudioChannelKind.Microphone, 1)]
    [InlineData(AudioChannelKind.Application, 2)]
    public void Kind_KeepsTheNumberAlreadyWrittenToSettingsFiles(AudioChannelKind kind, int persisted) =>
        Assert.Equal(persisted, (int)kind);

    /// <summary>
    /// Fails the moment a fourth kind appears, which is the prompt to come back here and pin its
    /// number too — the guard above is only worth anything if it covers the whole enum.
    /// </summary>
    [Fact]
    public void Kind_HasNoMembersBeyondTheThreePinnedHere() =>
        Assert.Equal(3, Enum.GetValues<AudioChannelKind>().Length);
}

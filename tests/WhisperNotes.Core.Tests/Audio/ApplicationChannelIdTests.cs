using WhisperNotes.Core.Audio;

namespace WhisperNotes.Core.Tests.Audio;

/// <summary>
/// The application id is the only part of a per-application input that survives a restart — the pid
/// does not — so what matters is that it is one canonical string per app no matter how the caller
/// spelled the executable, and that it can never be confused with a WASAPI endpoint id.
/// </summary>
public sealed class ApplicationChannelIdTests
{
    [Fact]
    public void ForExecutable_RoundTripsThroughExecutableOf()
    {
        string id = ApplicationChannelId.ForExecutable("teams.exe");

        Assert.Equal("app:teams.exe", id);
        Assert.Equal("teams.exe", ApplicationChannelId.ExecutableOf(id));
        Assert.True(ApplicationChannelId.IsApplicationId(id));
    }

    /// <summary>
    /// Two spellings of one executable have to collapse to one id, or the same app saved from the UI
    /// and from the CLI would come back as two separate inputs.
    /// </summary>
    [Theory]
    [InlineData("Teams.exe")]
    [InlineData("TEAMS.EXE")]
    [InlineData("teams.EXE")]
    public void ForExecutable_LowerCasesTheExecutable(string executable) =>
        Assert.Equal("app:teams.exe", ApplicationChannelId.ForExecutable(executable));

    [Theory]
    [InlineData("  ms-teams.exe")]
    [InlineData("ms-teams.exe   ")]
    [InlineData("\t ms-teams.exe \r\n")]
    public void ForExecutable_TrimsSurroundingWhitespace(string executable) =>
        Assert.Equal("app:ms-teams.exe", ApplicationChannelId.ForExecutable(executable));

    /// <summary>
    /// The whole point of the prefix: an endpoint id must never be mistaken for an application, or
    /// the capture factory would try to resolve a device that does not exist.
    /// </summary>
    [Theory]
    [InlineData("{0.0.0.00000000}.{9d2a4e12-3c8b-4f6e-9a1f-0b7c5d4e3f21}")]
    [InlineData("{0.0.1.00000000}.{b7e6c3a1-5d24-4f81-9c0e-2a6f8d1b4c37}")]
    [InlineData("legacy-endpoint")]
    [InlineData("")]
    [InlineData(null)]
    public void IsApplicationId_IsFalseForAnythingThatIsNotPrefixed(string? channelId)
    {
        Assert.False(ApplicationChannelId.IsApplicationId(channelId));
        Assert.Null(ApplicationChannelId.ExecutableOf(channelId));
    }

    [Fact]
    public void ForExecutable_RejectsANullExecutable() =>
        Assert.Throws<ArgumentNullException>(
            "executableName",
            () => ApplicationChannelId.ForExecutable(null!));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ForExecutable_RejectsABlankExecutable(string executable) =>
        Assert.Throws<ArgumentException>(
            "executableName",
            () => ApplicationChannelId.ForExecutable(executable));
}

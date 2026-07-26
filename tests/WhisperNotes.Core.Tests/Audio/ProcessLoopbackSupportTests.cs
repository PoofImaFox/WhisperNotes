using System.Globalization;
using WhisperNotes.Core.Audio;

namespace WhisperNotes.Core.Tests.Audio;

/// <summary>
/// This suite runs on whatever build the developer or CI machine happens to be, so it cannot assert
/// "supported" or "unsupported" outright — that would pass on one desk and fail on the next. What it
/// pins instead is that the gate agrees with the build number it claims to read, and that the two
/// public answers can never contradict each other.
/// </summary>
public sealed class ProcessLoopbackSupportTests
{
    /// <summary>
    /// 20348 is Windows Server 2022 RTM, the first build shipping
    /// <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>. Pinned as a literal on purpose: reading
    /// it back off the constant would assert nothing, and lowering it would put the app on a code
    /// path the OS cannot run.
    /// </summary>
    [Fact]
    public void MinimumBuild_IsTheDocumentedProcessLoopbackFloor() =>
        Assert.Equal(20348, ProcessLoopbackSupport.MinimumBuild);

    [Fact]
    public void CurrentBuild_ReportsTheRunningBuild() =>
        Assert.Equal(Environment.OSVersion.Version.Build, ProcessLoopbackSupport.CurrentBuild);

    [Fact]
    public void IsSupported_TracksTheRunningBuildAgainstTheFloor()
    {
        bool expected = OperatingSystem.IsWindows()
                        && Environment.OSVersion.Version.Build >= ProcessLoopbackSupport.MinimumBuild;

        Assert.Equal(expected, ProcessLoopbackSupport.IsSupported);
    }

    /// <summary>
    /// The reason string is what the UI and <c>devices</c> print to admit the fallback, so a null one
    /// on an unsupported machine would mean listing per-app inputs that silently record everything.
    /// </summary>
    [Fact]
    public void UnsupportedReason_IsPresentExactlyWhenCaptureIsNot()
    {
        string? reason = ProcessLoopbackSupport.UnsupportedReason;

        if (ProcessLoopbackSupport.IsSupported)
        {
            Assert.Null(reason);
            return;
        }

        Assert.NotNull(reason);
        Assert.Contains(
            ProcessLoopbackSupport.MinimumBuild.ToString(CultureInfo.CurrentCulture),
            reason,
            StringComparison.Ordinal);
        Assert.Contains(
            ProcessLoopbackSupport.CurrentBuild.ToString(CultureInfo.CurrentCulture),
            reason,
            StringComparison.Ordinal);
    }
}

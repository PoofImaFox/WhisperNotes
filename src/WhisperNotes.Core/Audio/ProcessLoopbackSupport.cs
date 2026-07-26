namespace WhisperNotes.Core.Audio;

/// <summary>
/// Probes whether this machine can capture a single application's audio via WASAPI process loopback.
/// </summary>
/// <remarks>
/// <para>
/// Per-application capture uses <c>ActivateAudioInterfaceAsync</c> with
/// <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>. Microsoft documents the floor as
/// "Windows 10 Build 20348", which reads like a Windows 10 servicing update but is not one: 20348 is the
/// Windows Server 2022 RTM build, and retail Windows 10 tops out at 19045 (22H2). In practice the API
/// needs Windows 11 (22000+) or Server 2022.
/// </para>
/// <para>
/// The build number is the honest gate, so that is what we test — Server 2022 satisfies it without
/// pretending to be Windows 11.
/// </para>
/// </remarks>
public static class ProcessLoopbackSupport
{
    /// <summary>First Windows build exposing <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>.</summary>
    public const int MinimumBuild = 20348;

    private static readonly bool Supported =
        OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= MinimumBuild;

    /// <summary>True when this machine can capture individual applications.</summary>
    public static bool IsSupported => Supported;

    /// <summary>The running OS build, for diagnostics and UI copy.</summary>
    public static int CurrentBuild => Environment.OSVersion.Version.Build;

    /// <summary>
    /// A user-facing explanation of why per-application capture is unavailable, or null when it is
    /// available.
    /// </summary>
    public static string? UnsupportedReason => Supported
        ? null
        : string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"Per-application capture needs Windows build {MinimumBuild} or later (Windows 11 / Server 2022). This machine reports build {CurrentBuild}, so application inputs record system audio instead.");
}

using System.Globalization;
using WhisperNotes.Core.Audio;

namespace WhisperNotes.App.ViewModels;

/// <summary>
/// One row of the channel picker. Loopback endpoints are what capture a Teams call; microphones
/// are listed too but are labelled unmistakably, because picking the wrong one silently records
/// nothing useful for an hour.
/// </summary>
/// <remarks>
/// Applications are the third kind and behave unlike the other two: an endpoint is still there
/// tomorrow, whereas an application channel exists only while that process is running. The row is
/// therefore labelled and grouped separately rather than mixed in with the hardware, so the user
/// can see at a glance which of their inputs depend on something being open.
/// </remarks>
public sealed class ChannelOptionViewModel(AudioChannel channel, bool showGroupHeader)
{
    public AudioChannel Channel { get; } = channel;

    public string Id => Channel.Id;

    public string Name => Channel.Name;

    public bool IsLoopback => Channel.Kind == AudioChannelKind.Loopback;

    public bool IsMicrophone => Channel.Kind == AudioChannelKind.Microphone;

    public bool IsApplication => Channel.Kind == AudioChannelKind.Application;

    public bool IsDefault => Channel.IsDefault;

    public string KindLabel => Channel.Kind switch
    {
        AudioChannelKind.Loopback => "LOOPBACK",
        AudioChannelKind.Application => "APP",
        _ => "MIC",
    };

    public string GroupHeader => Channel.Kind switch
    {
        AudioChannelKind.Loopback => "System output — loopback (this is what hears Teams)",
        AudioChannelKind.Application => "Applications — one app on its own, nothing else on the machine",
        _ => "Microphone input",
    };

    public bool ShowGroupHeader { get; } = showGroupHeader;

    /// <summary>The technical half of the row: what this source actually is, in one phrase.</summary>
    /// <remarks>
    /// An application is a process, not an endpoint, so it has no native mix format of its own —
    /// printing "48 kHz · stereo" beside it would state a fact about hardware the user did not
    /// pick. The executable is shown instead, because that is both the honest answer and the part
    /// of an application channel that survives a restart.
    /// </remarks>
    public string FormatText => IsApplication
        ? ApplicationFormat()
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{Channel.NativeSampleRate / 1000.0:0.#} kHz · {ChannelWord(Channel.NativeChannels)}");

    /// <summary>What the status bar and the collapsed combo box show.</summary>
    public string Summary => IsDefault ? $"{Name}  (default)" : Name;

    public string Detail => $"{KindLabel} · {FormatText}";

    private string ApplicationFormat()
    {
        // ExecutableName is the live enumeration's answer; the id is the persisted one. Either is
        // enough to name the process, and a channel that somehow has neither still gets a phrase
        // rather than an empty cell.
        string executable = Channel.ExecutableName
                            ?? ApplicationChannelId.ExecutableOf(Channel.Id)
                            ?? "application audio";

        // Pids are recycled between runs, so this is a diagnostic aid the user can match against
        // Task Manager — never identity. Absent it, the executable alone is the whole answer.
        return Channel.ProcessId > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{executable} · pid {Channel.ProcessId}")
            : executable;
    }

    private static string ChannelWord(int count) => count switch
    {
        1 => "mono",
        2 => "stereo",
        _ => string.Create(CultureInfo.CurrentCulture, $"{count} ch")
    };
}

using System.Globalization;
using WhisperNotes.Core.Audio;

namespace WhisperNotes.App.ViewModels;

/// <summary>
/// One row of the channel picker. Loopback endpoints are what capture a Teams call; microphones
/// are listed too but are labelled unmistakably, because picking the wrong one silently records
/// nothing useful for an hour.
/// </summary>
public sealed class ChannelOptionViewModel(AudioChannel channel, bool showGroupHeader)
{
    public AudioChannel Channel { get; } = channel;

    public string Id => Channel.Id;

    public string Name => Channel.Name;

    public bool IsLoopback => Channel.Kind == AudioChannelKind.Loopback;

    public bool IsMicrophone => Channel.Kind == AudioChannelKind.Microphone;

    public bool IsDefault => Channel.IsDefault;

    public string KindLabel => IsLoopback ? "LOOPBACK" : "MIC";

    public string GroupHeader => IsLoopback
        ? "System output — loopback (this is what hears Teams)"
        : "Microphone input";

    public bool ShowGroupHeader { get; } = showGroupHeader;

    public string FormatText => string.Create(
        CultureInfo.CurrentCulture,
        $"{Channel.NativeSampleRate / 1000.0:0.#} kHz · {ChannelWord(Channel.NativeChannels)}");

    /// <summary>What the status bar and the collapsed combo box show.</summary>
    public string Summary => IsDefault ? $"{Name}  (default)" : Name;

    public string Detail => $"{KindLabel} · {FormatText}";

    private static string ChannelWord(int count) => count switch
    {
        1 => "mono",
        2 => "stereo",
        _ => string.Create(CultureInfo.CurrentCulture, $"{count} ch")
    };
}

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.ViewModels;

/// <summary>One row of the whisper model picker, with cached/not-cached state.</summary>
public sealed partial class ModelOptionViewModel(WhisperModelSize size) : ObservableObject
{
    // IWhisperModelStore does not expose weight sizes, and knowing the download cost up front
    // matters on a tethered connection, so the published ggml sizes are mirrored here.
    private static readonly IReadOnlyDictionary<WhisperModelSize, long> ApproximateBytes =
        new Dictionary<WhisperModelSize, long>
        {
            [WhisperModelSize.Tiny] = 77L * 1024 * 1024,
            [WhisperModelSize.Base] = 141L * 1024 * 1024,
            [WhisperModelSize.Small] = 465L * 1024 * 1024,
            [WhisperModelSize.Medium] = 1462L * 1024 * 1024,
            [WhisperModelSize.LargeV3] = 2951L * 1024 * 1024,
            [WhisperModelSize.LargeV3Turbo] = 1549L * 1024 * 1024,
        };

    public WhisperModelSize Size { get; } = size;

    public string Name { get; } = size switch
    {
        WhisperModelSize.LargeV3 => "large-v3",
        WhisperModelSize.LargeV3Turbo => "large-v3-turbo",
        _ => size.ToString().ToLowerInvariant()
    };

    public string Hint { get; } = size switch
    {
        WhisperModelSize.Tiny => "fastest, drops names",
        WhisperModelSize.Base => "good default for meetings",
        WhisperModelSize.Small => "noticeably better on accents",
        WhisperModelSize.Medium => "slow on CPU, accurate",
        WhisperModelSize.LargeV3 => "best quality, needs a GPU to keep up",
        WhisperModelSize.LargeV3Turbo => "large quality, ~half the time",
        _ => string.Empty
    };

    public string SizeText { get; } = string.Create(
        CultureInfo.CurrentCulture,
        $"{ApproximateBytes[size] / 1024.0 / 1024.0:0} MB");

    [ObservableProperty] public partial bool IsDownloaded { get; set; }

    public string StatusText => IsDownloaded ? "on disk" : $"not downloaded · {SizeText}";

    partial void OnIsDownloadedChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

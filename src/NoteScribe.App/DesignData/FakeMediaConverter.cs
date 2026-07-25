using NoteScribe.Core.Media;

namespace NoteScribe.App.DesignData;

/// <summary>Reports ffmpeg as present so the status bar shows the healthy path.</summary>
internal sealed class FakeMediaConverter : IMediaConverter
{
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public string? FfmpegPath => @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";

    public Task<IReadOnlyList<MediaAudioStream>> ProbeAudioStreamsAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaAudioStream> streams =
        [
            new(1, "aac", 2, 48_000, "eng", "Meeting audio"),
            new(2, "aac", 1, 48_000, "eng", "Presenter mic"),
        ];

        return Task.FromResult(streams);
    }

    public async Task<string> ExtractAudioAsync(
        string inputPath,
        string outputWavPath,
        int? streamIndex,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = TimeSpan.FromMinutes(41);
        for (var i = 1; i <= 20; i++)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ConversionProgress(total * i / 20.0, total));
        }

        return outputWavPath;
    }
}

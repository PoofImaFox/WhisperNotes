using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.DesignData;

/// <summary>Pretends Tiny and Base are already cached; anything else downloads over a few seconds.</summary>
internal sealed class FakeWhisperModelStore : IWhisperModelStore
{
    private static readonly IReadOnlyDictionary<WhisperModelSize, long> Sizes =
        new Dictionary<WhisperModelSize, long>
        {
            [WhisperModelSize.Tiny] = 77_691_713,
            [WhisperModelSize.Base] = 147_951_465,
            [WhisperModelSize.Small] = 487_601_967,
            [WhisperModelSize.Medium] = 1_533_763_059,
            [WhisperModelSize.LargeV3] = 3_095_033_483,
            [WhisperModelSize.LargeV3Turbo] = 1_624_555_275,
        };

    private readonly HashSet<WhisperModelSize> _downloaded = [WhisperModelSize.Tiny, WhisperModelSize.Base];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WhisperNotes-SampleModels");

    public string GetModelPath(WhisperModelSize size) => Path.Combine(_root, FileName(size));

    public bool IsDownloaded(WhisperModelSize size)
    {
        lock (_downloaded)
        {
            return _downloaded.Contains(size);
        }
    }

    public async Task<string> EnsureDownloadedAsync(
        WhisperModelSize size,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsDownloaded(size))
        {
            progress?.Report(new ModelDownloadProgress(Sizes[size], Sizes[size]));
            return GetModelPath(size);
        }

        var total = Sizes[size];
        const int steps = 60;

        for (var i = 1; i <= steps; i++)
        {
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelDownloadProgress(total * i / steps, total));
        }

        lock (_downloaded)
        {
            _downloaded.Add(size);
        }

        return GetModelPath(size);
    }

    private static string FileName(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => "ggml-tiny.bin",
        WhisperModelSize.Base => "ggml-base.bin",
        WhisperModelSize.Small => "ggml-small.bin",
        WhisperModelSize.Medium => "ggml-medium.bin",
        WhisperModelSize.LargeV3 => "ggml-large-v3.bin",
        WhisperModelSize.LargeV3Turbo => "ggml-large-v3-turbo.bin",
        _ => "ggml-base.bin"
    };

    public static long ApproximateBytes(WhisperModelSize size) => Sizes[size];
}

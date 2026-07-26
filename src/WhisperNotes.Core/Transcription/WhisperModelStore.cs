using System.Buffers;
using System.Collections.Concurrent;

namespace WhisperNotes.Core.Transcription;

/// <summary>
/// Caches ggml weight files under a root directory and fetches missing ones from the
/// Whisper.net Hugging Face mirror.
/// </summary>
public sealed class WhisperModelStore : IWhisperModelStore
{
    // Whisper.net's own WhisperGgmlDownloader hands back a bare Stream with no length, so it can
    // never drive a percentage bar. We hit the same mirror it uses directly and read
    // Content-Length instead — a 1.5 GB medium download needs a real progress indicator.
    private const string DownloadRoot = "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/";

    /// <summary>Smallest real ggml file (tiny) is ~74 MB; anything under this is a truncated leftover.</summary>
    private const long MinimumPlausibleModelBytes = 1024 * 1024;

    private const int CopyBufferBytes = 128 * 1024;

    /// <summary>One progress tick per megabyte. Reporting every read would flood a UI-thread binding.</summary>
    private const long ProgressReportIntervalBytes = 1024 * 1024;

    // HttpClient.Timeout covers the response body too, so the 100 s default would abort any model
    // bigger than a few hundred megabytes. The cancellation token is the real deadline here.
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly ConcurrentDictionary<WhisperModelSize, SemaphoreSlim> _downloadGates = new();
    private readonly HttpClient _http;

    public WhisperModelStore(string modelsRoot, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        Root = Path.GetFullPath(modelsRoot);
        _http = httpClient ?? SharedClient;
    }

    /// <summary>Absolute path of the directory the weights are cached in.</summary>
    public string Root { get; }

    public static string FileNameFor(WhisperModelSize size) => $"ggml-{ModelName(size)}.bin";

    public string GetModelPath(WhisperModelSize size) => Path.Combine(Root, FileNameFor(size));

    public bool IsDownloaded(WhisperModelSize size)
    {
        FileInfo info = new(GetModelPath(size));
        return info.Exists && info.Length >= MinimumPlausibleModelBytes;
    }

    public async Task<string> EnsureDownloadedAsync(
        WhisperModelSize size,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string destination = GetModelPath(size);
        if (IsDownloaded(size))
        {
            return destination;
        }

        SemaphoreSlim gate = _downloadGates.GetOrAdd(size, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A concurrent caller may have finished while we waited.
            if (IsDownloaded(size))
            {
                return destination;
            }

            await DownloadAsync(size, destination, progress, cancellationToken).ConfigureAwait(false);
            return destination;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadAsync(
        WhisperModelSize size,
        string destination,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);

        string fileName = FileNameFor(size);
        // Download beside the target and rename only once the bytes are all on disk, so a crash or
        // a cancel can never leave a half-written file that whisper would later fail to load.
        string tempPath = destination + ".tmp";
        long bytesRead = 0;

        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync(DownloadRoot + fileName, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            progress?.Report(new ModelDownloadProgress(0, total));

            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream output = new(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                try
                {
                    long lastReported = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        bytesRead += read;

                        if (bytesRead - lastReported >= ProgressReportIntervalBytes)
                        {
                            lastReported = bytesRead;
                            progress?.Report(new ModelDownloadProgress(bytesRead, total));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (total is > 0 && bytesRead != total.Value)
            {
                throw new IOException(
                    $"Truncated download of '{fileName}': expected {total.Value} bytes but received {bytesRead}.");
            }

            File.Move(tempPath, destination, overwrite: true);
            progress?.Report(new ModelDownloadProgress(bytesRead, bytesRead));
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A partial file we cannot remove is still never promoted to the real name.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ModelName(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => "tiny",
        WhisperModelSize.Base => "base",
        WhisperModelSize.Small => "small",
        WhisperModelSize.Medium => "medium",
        WhisperModelSize.LargeV3 => "large-v3",
        WhisperModelSize.LargeV3Turbo => "large-v3-turbo",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unknown whisper model size.")
    };
}

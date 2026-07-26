using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Diarization;

/// <summary>Resolves and downloads the speaker-embedding weights.</summary>
public interface ISpeakerModelStore
{
    string ModelPath { get; }

    bool IsDownloaded { get; }

    /// <summary>
    /// Downloads the model if absent and returns the local path. Like the whisper store, this
    /// writes to a temp file and moves it into place, so a cancelled download can never leave
    /// something that later loads as a corrupt model.
    /// </summary>
    Task<string> EnsureDownloadedAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fetches the 3D-Speaker ERes2Net voice-print model into the same models directory the whisper
/// weights live in.
/// </summary>
/// <remarks>
/// Chosen over the WeSpeaker ResNet34 that pyannote uses for a licensing reason rather than an
/// accuracy one: both separate voices about equally well, but WeSpeaker's published weights inherit
/// VoxCeleb's CC-BY-4.0 terms and so carry an attribution obligation, where this model is
/// distributed under Apache-2.0. It is also the better-behaved of the two — it declares its own
/// feature normalisation in metadata and takes audio at the amplitude the rest of this codebase
/// already uses, so there is one fewer convention to get silently wrong.
/// </remarks>
/// <remarks>
/// This deliberately mirrors <see cref="WhisperModelStore"/> rather than generalising it. The two
/// have the same mechanics but different contracts: whisper has a family of sizes the user chooses
/// between, and this has exactly one model, so widening the whisper store to a string key would
/// make every existing call site less clear in order to save about sixty lines here.
/// </remarks>
public sealed class SpeakerModelStore : ISpeakerModelStore
{
    /// <summary>
    /// Published in the sherpa-onnx model zoo. The release tag is misspelled upstream — that is not
    /// a typo here, and correcting it gives a 404.
    /// </summary>
    private const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx";

    private const string FileName = "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx";

    /// <summary>
    /// The published asset is 26,485,263 bytes. Unlike the whisper weights this one has a hash we
    /// can pin, and it is worth pinning: a truncated or substituted model still loads and still
    /// returns 192 numbers, so a corrupt download would not fail — it would quietly attribute the
    /// wrong people to the wrong lines.
    /// </summary>
    private const string ExpectedSha256 = "C59158379255AD66E161679CCA6AF8D52D51E389E3224AB7D7A7BAAE295C2DB5";

    /// <summary>Anything much under the real 26 MB is a truncated leftover rather than a model.</summary>
    private const long MinimumPlausibleModelBytes = 20L * 1024 * 1024;

    /// <summary>One progress tick per megabyte, matching the whisper store so the bar behaves the same.</summary>
    private const long ProgressReportIntervalBytes = 1024 * 1024;

    private const int CopyBufferBytes = 128 * 1024;

    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public SpeakerModelStore(string modelsRoot, HttpClient? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        Root = Path.GetFullPath(modelsRoot);
        _client = client ?? SharedClient;
    }

    public string Root { get; }

    public string ModelPath => Path.Combine(Root, FileName);

    public bool IsDownloaded
    {
        get
        {
            FileInfo info = new(ModelPath);
            return info.Exists && info.Length >= MinimumPlausibleModelBytes;
        }
    }

    public async Task<string> EnsureDownloadedAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsDownloaded)
        {
            return ModelPath;
        }

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A concurrent caller may have finished while we waited.
            if (IsDownloaded)
            {
                return ModelPath;
            }

            Directory.CreateDirectory(Root);
            await DownloadAsync(ModelPath, progress, cancellationToken).ConfigureAwait(false);
            return ModelPath;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task DownloadAsync(
        string destination,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = destination + ".tmp";

        try
        {
            using HttpResponseMessage response = await _client
                .GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            progress?.Report(new ModelDownloadProgress(0, total));

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream target = new(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                try
                {
                    long read = 0;
                    long reported = 0;

                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        read += count;

                        if (read - reported >= ProgressReportIntervalBytes)
                        {
                            reported = read;
                            progress?.Report(new ModelDownloadProgress(read, total));
                        }
                    }

                    if (total is { } expected && read != expected)
                    {
                        throw new IOException(
                            $"Truncated download of {FileName}: expected "
                            + expected.ToString(CultureInfo.InvariantCulture)
                            + " bytes, got " + read.ToString(CultureInfo.InvariantCulture) + ".");
                    }

                    progress?.Report(new ModelDownloadProgress(read, read));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            await VerifyAsync(tempPath, cancellationToken).ConfigureAwait(false);

            // Renamed only once the bytes are all on disk and verified, so a crash or a cancel can
            // never leave a half-written file that would later load as a working model.
            File.Move(tempPath, destination, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);

        if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"{FileName} does not match its expected checksum (got {actual}). "
                + "The download was corrupted, or the published model has changed.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

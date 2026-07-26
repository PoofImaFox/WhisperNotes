using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperNotes.Core.Media;

/// <summary>
/// <see cref="IMediaConverter"/> backed by the ffmpeg/ffprobe command line tools.
/// </summary>
public sealed partial class FfmpegMediaConverter : IMediaConverter
{
    private const int StderrTailLines = 20;

    private readonly string? _ffprobePath;

    /// <summary>Set when an explicitly configured path did not resolve, so the reason survives to the error message.</summary>
    private readonly string? _resolutionError;

    /// <param name="explicitFfmpegPath">
    /// Path to ffmpeg.exe, or to the directory containing it. Null resolves from PATH and then
    /// from the usual install locations.
    /// </param>
    public FfmpegMediaConverter(string? explicitFfmpegPath = null)
    {
        // An explicit path is a decision, not a hint. Falling back to PATH when the user named a
        // location would mean a typo'd --ffmpeg silently runs some other ffmpeg — or appears to
        // work until the day PATH doesn't have one.
        if (!string.IsNullOrWhiteSpace(explicitFfmpegPath))
        {
            FfmpegPath = ResolveFromHint(explicitFfmpegPath, ExecutableName("ffmpeg"), "ffmpeg");
            _ffprobePath = FfmpegPath is null
                ? null
                : ResolveTool("ffprobe", SiblingHint(FfmpegPath, explicitFfmpegPath));

            if (FfmpegPath is null)
            {
                _resolutionError =
                    $"ffmpeg was not found at the configured path '{explicitFfmpegPath}'. " +
                    "Point FfmpegPath (or --ffmpeg) at ffmpeg.exe or the folder containing it, or clear it to search PATH.";
            }
            else if (_ffprobePath is null)
            {
                _resolutionError =
                    $"ffmpeg was found at '{FfmpegPath}' but ffprobe was not. They ship together — check the install.";
            }

            return;
        }

        FfmpegPath = ResolveTool("ffmpeg", null);
        _ffprobePath = ResolveTool("ffprobe", SiblingHint(FfmpegPath, null));
    }

    public bool IsAvailable => FfmpegPath is not null && _ffprobePath is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : _resolutionError ??
          "ffmpeg (and ffprobe) could not be found. Install them and put them on PATH, or pass --ffmpeg <path>.";

    public string? FfmpegPath { get; }

    /// <summary>Path to the resolved ffprobe binary, for diagnostics.</summary>
    public string? FfprobePath => _ffprobePath;

    public async Task<IReadOnlyList<MediaAudioStream>> ProbeAudioStreamsAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var ffprobe = RequireFfprobe();
        if (!File.Exists(inputPath))
        {
            throw new MediaConversionException($"Input file not found: {inputPath}");
        }

        var result = await RunAsync(
            ffprobe,
            ["-v", "quiet", "-print_format", "json", "-show_streams", "-select_streams", "a", inputPath],
            onStandardErrorLine: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw Failed("ffprobe", result);
        }

        return ParseStreams(result.StandardOutput, inputPath);
    }

    public async Task<string> ExtractAudioAsync(
        string inputPath,
        string outputWavPath,
        int? streamIndex,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputWavPath);
        var ffmpeg = RequireFfmpeg();
        if (!File.Exists(inputPath))
        {
            throw new MediaConversionException($"Input file not found: {inputPath}");
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputWavPath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        TimeSpan? total = progress is null
            ? null
            : await TryGetDurationAsync(inputPath, cancellationToken).ConfigureAwait(false);

        var map = streamIndex is { } index
            ? $"0:{index.ToString(CultureInfo.InvariantCulture)}"
            : "0:a:0";

        string[] args =
        [
            "-hide_banner", "-nostdin", "-stats",
            "-i", inputPath,
            "-vn",
            "-map", map,
            "-acodec", "pcm_s16le",
            "-ar", "16000",
            "-ac", "1",
            "-y",
            outputWavPath
        ];

        var result = await RunAsync(
            ffmpeg,
            args,
            onStandardErrorLine: progress is null ? null : line =>
            {
                if (TryParseProgressTime(line, out var processed))
                {
                    progress.Report(new ConversionProgress(processed, total));
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw Failed("ffmpeg", result);
        }

        if (!File.Exists(outputWavPath))
        {
            throw new MediaConversionException(
                $"ffmpeg reported success but produced no file at {outputWavPath}.");
        }

        if (progress is not null && total is { } known)
        {
            progress.Report(new ConversionProgress(known, known));
        }

        return outputWavPath;
    }

    /// <summary>Total media duration from ffprobe, or null when the container does not report one.</summary>
    public async Task<TimeSpan?> TryGetDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        var ffprobe = _ffprobePath;
        if (ffprobe is null)
        {
            return null;
        }

        ProcessResult result;
        try
        {
            result = await RunAsync(
                ffprobe,
                ["-v", "quiet", "-print_format", "json", "-show_format", inputPath],
                onStandardErrorLine: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (MediaConversionException)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var duration) &&
                duration.ValueKind == JsonValueKind.String &&
                double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch (JsonException)
        {
            // Treat unparseable output as "duration unknown" rather than failing the conversion.
        }

        return null;
    }

    private static IReadOnlyList<MediaAudioStream> ParseStreams(string json, string inputPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException ex)
        {
            throw new MediaConversionException($"Could not parse ffprobe output for {inputPath}.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<MediaAudioStream>();
            foreach (var stream in streams.EnumerateArray())
            {
                if (ToAudioStream(stream, results.Count) is { } audio)
                {
                    results.Add(audio);
                }
            }

            return results;
        }
    }

    /// <summary>
    /// One entry of ffprobe's <c>streams</c> array, or null when it is not an object we can read.
    /// </summary>
    /// <param name="fallbackIndex">Used when the entry omits <c>index</c>, which some muxers do.</param>
    private static MediaAudioStream? ToAudioStream(JsonElement stream, int fallbackIndex)
    {
        if (stream.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var index = stream.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var i)
            ? i
            : fallbackIndex;

        var codec = stream.TryGetProperty("codec_name", out var codecElement)
            ? codecElement.GetString() ?? "unknown"
            : "unknown";

        var channels = stream.TryGetProperty("channels", out var channelsElement) &&
                       channelsElement.TryGetInt32(out var c)
            ? c
            : 0;

        string? language = null;
        string? title = null;
        if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            language = ReadTag(tags, "language");
            title = ReadTag(tags, "title");
        }

        return new MediaAudioStream(index, codec, channels, ReadSampleRate(stream), language, title);
    }

    // ffprobe quotes sample_rate as a string, but a few builds emit it as a bare number.
    private static int ReadSampleRate(JsonElement stream)
    {
        if (!stream.TryGetProperty("sample_rate", out var rate))
        {
            return 0;
        }

        if (rate.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(rate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        return rate.ValueKind == JsonValueKind.Number && rate.TryGetInt32(out var number) ? number : 0;
    }

    // Container tag keys vary in case between muxers ("language" vs "LANGUAGE").
    private static string? ReadTag(JsonElement tags, string name)
    {
        foreach (var property in tags.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static bool TryParseProgressTime(string line, out TimeSpan processed)
    {
        processed = default;
        var match = ProgressTimeRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(match.Groups[3].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        processed = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }

    [GeneratedRegex(@"time=\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressTimeRegex();

    private string RequireFfmpeg() =>
        FfmpegPath ?? throw new MediaConversionException(
            _resolutionError ??
            "ffmpeg was not found. Install it and put it on PATH, or set FfmpegPath in settings.");

    private string RequireFfprobe() =>
        _ffprobePath ?? throw new MediaConversionException(
            _resolutionError ??
            "ffprobe was not found. It ships with ffmpeg — install it and put it on PATH, or set FfmpegPath in settings.");

    private static MediaConversionException Failed(string tool, ProcessResult result)
    {
        var tail = result.StandardErrorTail.Count == 0
            ? "(no diagnostics)"
            : string.Join(Environment.NewLine, result.StandardErrorTail);

        return new MediaConversionException(
            $"{tool} exited with code {result.ExitCode}.{Environment.NewLine}{tail}");
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string>? onStandardErrorLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        // ArgumentList quotes each item itself: paths with spaces or quotes stay intact.
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new MediaConversionException($"Could not start '{executable}'.", ex);
        }

        var tail = new Queue<string>(StderrTailLines);
        await using var registration = cancellationToken.Register(static state => TryKill((Process)state!), process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = PumpLinesAsync(process.StandardError, line =>
        {
            if (tail.Count == StderrTailLines)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
            onStandardErrorLine?.Invoke(line);
        });

        var standardOutput = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return new ProcessResult(process.ExitCode, standardOutput, [.. tail]);
    }

    // ffmpeg terminates its progress lines with '\r', so ReadLineAsync would stall until the
    // whole conversion finished. Split on either terminator instead.
    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[2048];
        var current = new StringBuilder();

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break; // Process was killed; the pipe is gone.
            }

            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (current.Length > 0)
                    {
                        onLine(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        if (current.Length > 0)
        {
            onLine(current.ToString());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // Already gone, or we lost the race with normal exit.
        }
    }

    private static string? SiblingHint(string? resolvedFfmpeg, string? explicitHint)
    {
        if (resolvedFfmpeg is not null)
        {
            var directory = Path.GetDirectoryName(resolvedFfmpeg);
            if (!string.IsNullOrEmpty(directory))
            {
                return directory;
            }
        }

        return explicitHint;
    }

    private static string ExecutableName(string toolName) =>
        OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

    private static string? ResolveTool(string toolName, string? hint)
    {
        var fileName = ExecutableName(toolName);

        if (!string.IsNullOrWhiteSpace(hint))
        {
            var candidate = ResolveFromHint(hint, fileName, toolName);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        foreach (var directory in CandidateDirectories())
        {
            var candidate = SafeCombine(directory, fileName);
            if (candidate is not null && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string? ResolveFromHint(string hint, string fileName, string toolName)
    {
        try
        {
            if (Directory.Exists(hint))
            {
                var inDirectory = Path.Combine(hint, fileName);
                if (File.Exists(inDirectory))
                {
                    return Path.GetFullPath(inDirectory);
                }

                return null;
            }

            if (File.Exists(hint))
            {
                // The hint names ffmpeg itself; ffprobe lives beside it.
                var name = Path.GetFileNameWithoutExtension(hint);
                if (string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(hint);
                }

                var directory = Path.GetDirectoryName(Path.GetFullPath(hint));
                if (!string.IsNullOrEmpty(directory))
                {
                    var sibling = Path.Combine(directory, fileName);
                    if (File.Exists(sibling))
                    {
                        return sibling;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Malformed hint — fall through to PATH resolution.
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return entry.Trim('"');
            }
        }

        foreach (var directory in CommonInstallDirectories())
        {
            yield return directory;
        }
    }

    private static IEnumerable<string> CommonInstallDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        string?[] roots =
        [
            @"C:\ffmpeg\bin",
            @"C:\Program Files\ffmpeg\bin",
            @"C:\ProgramData\chocolatey\bin",
            SafeCombine(programFiles, "ffmpeg", "bin"),
            SafeCombine(localAppData, "Microsoft", "WinGet", "Links"),
            SafeCombine(userProfile, "scoop", "shims"),
            "/usr/bin",
            "/usr/local/bin",
            "/opt/homebrew/bin"
        ];

        foreach (var root in roots)
        {
            if (!string.IsNullOrEmpty(root))
            {
                yield return root;
            }
        }

        // Zip installs land as C:\ffmpeg-<version>-full_build\bin and similar.
        foreach (var directory in GlobVersionedInstalls())
        {
            yield return directory;
        }
    }

    private static IReadOnlyList<string> GlobVersionedInstalls()
    {
        var results = new List<string>();
        string?[] searchRoots =
        [
            Path.GetPathRoot(Environment.SystemDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        ];

        foreach (var searchRoot in searchRoots)
        {
            if (string.IsNullOrEmpty(searchRoot) || !Directory.Exists(searchRoot))
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(searchRoot, "ffmpeg*", SearchOption.TopDirectoryOnly))
                {
                    results.Add(directory);
                    var bin = Path.Combine(directory, "bin");
                    if (Directory.Exists(bin))
                    {
                        results.Add(bin);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable root — skip it.
            }
        }

        return results;
    }

    private static string? SafeCombine(string? first, params string[] rest)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        try
        {
            return rest.Length == 0 ? first : Path.Combine([first, .. rest]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private readonly record struct ProcessResult(
        int ExitCode,
        string StandardOutput,
        IReadOnlyList<string> StandardErrorTail);
}

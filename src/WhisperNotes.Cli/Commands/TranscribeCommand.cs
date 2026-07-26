using System.CommandLine;
using System.Globalization;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Composition;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Media;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Cli.Commands;

/// <summary>
/// The <c>--video</c> path: ffmpeg extracts one audio stream to 16 kHz mono, then the same local
/// whisper runs over it and writes the same kind of session the live path does.
/// </summary>
internal static class TranscribeCommand
{
    private const int LabelWidth = 13;

    /// <summary>
    /// 30 s per decode: whisper's own window, so nothing is re-encoded internally, and short enough
    /// that a long recording never holds more than a few megabytes of float at once.
    /// </summary>
    private const int ChunkSamples = AudioFrame.SampleRate * 30;

    /// <summary>
    /// RMS below this is digital silence, not quiet speech (about -66 dBFS). Handing silence to
    /// whisper reliably produces invented captions, which is the last thing a billable record needs.
    /// </summary>
    private const float SilenceFloor = 0.0005f;

    private static readonly Option<string> VideoOption = new("--video", "-v", "--input", "-i")
    {
        Description = "Input media. Any container ffmpeg reads.",
        HelpName = "path",
        Required = true
    };

    private static readonly Option<int?> StreamOption = new("--stream")
    {
        Description = "Which audio stream to take, from --list-streams. Defaults to the first.",
        HelpName = "n"
    };

    private static readonly Option<bool> ListStreamsOption = new("--list-streams")
    {
        Description = "Print the file's audio streams and exit without transcribing."
    };

    private static readonly Option<string> OutputOption = new("--output", "-o")
    {
        Description = "Override where this session is written. Defaults to the notes root.",
        HelpName = "dir"
    };

    private static readonly Option<bool> DiarizeOption = CliOptions.Diarize();
    private static readonly Option<bool> NoDiarizeOption = CliOptions.NoDiarize();
    private static readonly Option<int?> MaxSpeakersOption = CliOptions.MaxSpeakers();

    private static readonly Option<string> TitleOption = CliOptions.Title();
    private static readonly Option<string> ProjectOption = CliOptions.Project();
    private static readonly Option<string> ModelOption = CliOptions.Model();
    private static readonly Option<string> LanguageOption = CliOptions.Language();
    private static readonly Option<string[]> TagOption = CliOptions.Tag();
    private static readonly Option<string> PromptOption = CliOptions.Prompt();
    private static readonly Option<bool> KeepAudioOption = CliOptions.KeepAudio();
    private static readonly Option<int?> ThreadsOption = CliOptions.Threads();

    public static Command Create()
    {
        Command command = new("transcribe", "Transcribe a recorded video or audio file into a new session.");

        command.Options.Add(VideoOption);
        command.Options.Add(StreamOption);
        command.Options.Add(ListStreamsOption);
        command.Options.Add(TitleOption);
        command.Options.Add(ProjectOption);
        command.Options.Add(ModelOption);
        command.Options.Add(LanguageOption);
        command.Options.Add(TagOption);
        command.Options.Add(PromptOption);
        command.Options.Add(KeepAudioOption);
        command.Options.Add(ThreadsOption);
        command.Options.Add(OutputOption);
        command.Options.Add(DiarizeOption);
        command.Options.Add(NoDiarizeOption);
        command.Options.Add(MaxSpeakersOption);

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, environment => ExecuteAsync(parseResult, environment, cancellationToken)));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        CliEnvironment environment,
        CancellationToken cancellationToken)
    {
        ConsoleOutput console = environment.Console;
        var input = ResolveInput(parseResult.GetRequiredValue(VideoOption));

        SessionSupport.ApplyTranscriptionOptions(
            parseResult, environment.Settings, ModelOption, LanguageOption, ThreadsOption, PromptOption);

        SpeakerSupport.ApplyOptions(
            parseResult,
            environment.Settings,
            DiarizeOption,
            NoDiarizeOption,
            MaxSpeakersOption);

        if (parseResult.GetValue(OutputOption) is { Length: > 0 } output)
        {
            environment.Settings.NotesRoot = Path.GetFullPath(output);
        }

        ModelDownloadReporter reporter = new(console);
        await using WhisperNotesServices services = WhisperNotesServices.Create(environment.Settings, reporter);

        RequireFfmpeg(services, console);

        IReadOnlyList<MediaAudioStream> streams = await services.Media
            .ProbeAudioStreamsAsync(input, cancellationToken)
            .ConfigureAwait(false);

        if (parseResult.GetValue(ListStreamsOption))
        {
            return ListStreams(console, streams, input);
        }

        if (streams.Count == 0)
        {
            throw new CliException(ExitCode.MediaFailed, $"{input} contains no audio streams — there is nothing to transcribe.");
        }

        int? stream = ResolveStream(parseResult.GetValue(StreamOption), streams, input);
        var keepAudio = parseResult.GetValue(KeepAudioOption) || environment.Settings.KeepSessionAudio;

        console.Line();
        await SessionSupport.PrepareModelAsync(services, reporter, console, LabelWidth, cancellationToken)
            .ConfigureAwait(false);

        NoteSession session = await services.Notes.CreateSessionAsync(
            parseResult.GetValue(TitleOption) ?? Path.GetFileNameWithoutExtension(input),
            parseResult.GetValue(ProjectOption) ?? environment.Settings.DefaultProject,
            $"video: {Path.GetFileName(input)}" + (stream is { } index ? $" (stream #{index.ToString(CultureInfo.InvariantCulture)})" : string.Empty),
            SessionSupport.Tags(parseResult, TagOption),
            ModelSizes.Name(services.Settings.Model),
            cancellationToken).ConfigureAwait(false);

        console.Field("session", services.Notes.GetSessionDirectory(session.Id), LabelWidth);

        var wavPath = keepAudio
            ? SessionSupport.AudioPath(services, session.Id)
            : Path.Combine(Path.GetTempPath(), $"whispernotes-{session.Id}.wav");

        var entries = 0;

        // Read before the WAV is cleaned up. The session's duration is the length of the meeting,
        // not the time the decoder spent on it — reporting "00:00:01" for an hour-long recording
        // would be worse than useless on a record someone bills from.
        TimeSpan? mediaDuration = null;
        ISpeakerAttributor? attributor = null;

        try
        {
            await ExtractAsync(services, console, input, wavPath, stream, cancellationToken).ConfigureAwait(false);
            mediaDuration = TryGetDuration(services, wavPath);
            attributor = await SpeakerSupport.PrepareAsync(services, console, cancellationToken).ConfigureAwait(false);

            List<NoteEntry> written = await DecodeAsync(services, console, session, wavPath, attributor, cancellationToken)
                .ConfigureAwait(false);

            entries = written.Count;

            // Before the finalize below, so the rendered notes.md carries the speaker labels rather
            // than needing a second render to pick them up.
            await SpeakerSupport
                .AttributeAsync(services, console, session, written, attributor, LabelWidth)
                .ConfigureAwait(false);
        }
        finally
        {
            attributor?.Dispose();

            if (!keepAudio)
            {
                TryDelete(wavPath, console);
            }

            // The session is finalized even on failure: whatever was decoded before the error is
            // still the user's record of the meeting.
            await services.Notes
                .FinalizeSessionAsync(session.Id, CancellationToken.None, mediaDuration)
                .ConfigureAwait(false);
        }

        if (entries == 0)
        {
            console.Warn($"no speech was decoded from {Path.GetFileName(input)} — check that the chosen stream carries audio.");
        }

        console.Field("notes", SessionSupport.NotesPath(services, session.Id), LabelWidth);

        return cancellationToken.IsCancellationRequested ? ExitCode.Interrupted : ExitCode.Success;
    }

    private static async Task ExtractAsync(
        WhisperNotesServices services,
        ConsoleOutput console,
        string input,
        string wavPath,
        int? stream,
        CancellationToken cancellationToken)
    {
        var mapping = stream is { } index
            ? $"(ffmpeg, stream #{index.ToString(CultureInfo.InvariantCulture)} -> 16 kHz mono)"
            : "(ffmpeg, first audio stream -> 16 kHz mono)";

        // Disposal only closes the console line, and every path below already does that explicitly;
        // 'using' is the guarantee that a future path added here cannot leave a half-drawn bar.
        using ProgressIndicator indicator = new(console, "extracting", LabelWidth);
        try
        {
            await services.Media.ExtractAudioAsync(
                input,
                wavPath,
                stream,
                new Progress<ConversionProgress>(p => indicator.Report(p.Fraction, mapping)),
                cancellationToken).ConfigureAwait(false);

            indicator.Complete(1, mapping);
        }
        catch
        {
            indicator.Abandon();
            throw;
        }
    }

    private static async Task<List<NoteEntry>> DecodeAsync(
        WhisperNotesServices services,
        ConsoleOutput console,
        NoteSession session,
        string wavPath,
        ISpeakerAttributor? attributor,
        CancellationToken cancellationToken)
    {
        TimeSpan total = services.WavReader.GetDuration(wavPath);
        TranscriptionOptions options = services.Settings.ToTranscriptionOptions();

        await using ITranscriber transcriber = await services.Transcribers
            .CreateAsync(options, cancellationToken)
            .ConfigureAwait(false);

        using ProgressIndicator indicator = new(console, "transcribing", LabelWidth);
        var offset = TimeSpan.Zero;
        List<NoteEntry> entries = [];

        try
        {
            await foreach (ReadOnlyMemory<float> chunk in services.WavReader
                .ReadChunksAsync(wavPath, ChunkSamples, cancellationToken)
                .ConfigureAwait(false))
            {
                TimeSpan duration = TimeSpan.FromSeconds((double)chunk.Length / AudioFrame.SampleRate);

                if (HasSignal(chunk.Span))
                {
                    await foreach (TranscriptSegment segment in transcriber
                        .TranscribeAsync(chunk, offset, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        if (string.IsNullOrWhiteSpace(segment.Text))
                        {
                            continue;
                        }

                        NoteEntry entry = SessionSupport.ToEntry(session, segment);

                        await services.Notes
                            .AppendEntryAsync(session.Id, entry, CancellationToken.None)
                            .ConfigureAwait(false);

                        entries.Add(entry);

                        // Voice-printed here rather than on a second pass over the file: this buffer
                        // is the very audio the line was decoded from, and it is already in memory.
                        attributor?.Observe(segment.Start, segment.End, chunk.Span, offset);
                    }
                }

                offset += duration;
                indicator.Report(
                    total > TimeSpan.Zero ? offset / total : null,
                    $"{Format.Clock(offset)} / {Format.Clock(total)}");
            }

            indicator.Complete(1, $"{Format.Clock(total)} / {Format.Clock(total)}");
        }
        catch (OperationCanceledException)
        {
            indicator.Abandon();
            console.Warn($"interrupted after {Format.Clock(offset)}; the notes hold everything decoded so far.");
        }
        catch
        {
            indicator.Abandon();
            throw;
        }

        return entries;
    }

    private static int ListStreams(ConsoleOutput console, IReadOnlyList<MediaAudioStream> streams, string input)
    {
        console.Result();

        if (streams.Count == 0)
        {
            console.Result($"  {Path.GetFileName(input)} contains no audio streams.");
            return ExitCode.Success;
        }

        foreach (MediaAudioStream stream in streams)
        {
            console.Result("  " + stream.Describe());
        }

        console.Result();
        return ExitCode.Success;
    }

    private static string ResolveInput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliException(ExitCode.Usage, "--video needs a path to a media file.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliException(ExitCode.Usage, $"'{value}' is not a usable file path.", ex);
        }

        if (Directory.Exists(full))
        {
            throw new CliException(ExitCode.Usage, $"{full} is a directory. --video takes a single media file.");
        }

        if (!File.Exists(full))
        {
            throw new CliException(ExitCode.Usage, $"No such file: {full}");
        }

        return full;
    }

    private static int? ResolveStream(int? requested, IReadOnlyList<MediaAudioStream> streams, string input)
    {
        if (requested is not { } index)
        {
            return null;
        }

        if (streams.Any(s => s.Index == index))
        {
            return index;
        }

        var available = string.Join(
            Environment.NewLine,
            streams.Select(static s => "  " + s.Describe()));

        throw new CliException(
            ExitCode.Usage,
            $"{Path.GetFileName(input)} has no audio stream #{index.ToString(CultureInfo.InvariantCulture)}. Available:"
            + Environment.NewLine + available);
    }

    private static TimeSpan? TryGetDuration(WhisperNotesServices services, string wavPath)
    {
        try
        {
            return services.WavReader.GetDuration(wavPath);
        }
        catch (Exception)
        {
            // Duration is metadata, not the notes. Never let it sink a finalize.
            return null;
        }
    }

    private static void RequireFfmpeg(WhisperNotesServices services, ConsoleOutput console)
    {
        if (!services.Media.IsAvailable)
        {
            throw new CliException(
                ExitCode.MediaFailed,
                services.Media.UnavailableReason ??
                "ffmpeg (and ffprobe) could not be found. Install them and put them on PATH, or pass --ffmpeg <path>.");
        }

        console.Diagnostic($"ffmpeg     {services.Media.FfmpegPath}");
    }

    private static bool HasSignal(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return samples.Length > 0 && Math.Sqrt(sum / samples.Length) >= SilenceFloor;
    }

    private static void TryDelete(string path, ConsoleOutput console)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.Warn($"could not delete the temporary audio at {path}: {ex.Message}");
        }
    }
}

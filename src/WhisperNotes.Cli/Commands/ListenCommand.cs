using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using WhisperNotes.Cli.Audio;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Composition;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Cli.Commands;

/// <summary>
/// Live capture and dictation. Runs until Ctrl+C, then finalizes the session and writes notes.md.
/// </summary>
internal static class ListenCommand
{
    private const int LabelWidth = 9;
    private const int FinalLabelWidth = 11;

    private static readonly Option<string> ChannelOption = new("--channel", "-c")
    {
        Description = "Endpoint from 'whispernotes devices'. Defaults to the last used, else the default render loopback.",
        HelpName = "id"
    };

    private static readonly Option<string> TitleOption = CliOptions.Title();
    private static readonly Option<string> ProjectOption = CliOptions.Project();
    private static readonly Option<string> ModelOption = CliOptions.Model();
    private static readonly Option<string> LanguageOption = CliOptions.Language();
    private static readonly Option<string[]> TagOption = CliOptions.Tag();
    private static readonly Option<string> PromptOption = CliOptions.Prompt();
    private static readonly Option<bool> KeepAudioOption = CliOptions.KeepAudio();
    private static readonly Option<int?> ThreadsOption = CliOptions.Threads();
    private static readonly Option<bool> DiarizeOption = CliOptions.Diarize();
    private static readonly Option<bool> NoDiarizeOption = CliOptions.NoDiarize();
    private static readonly Option<int?> MaxSpeakersOption = CliOptions.MaxSpeakers();

    private static readonly Option<bool> QuietOption = new("--quiet")
    {
        Description = "Suppress the live transcript echo; only print the final path."
    };

    public static Command Create()
    {
        Command command = new("listen", "Capture an audio endpoint live and dictate into a new session.");

        command.Options.Add(ChannelOption);
        command.Options.Add(TitleOption);
        command.Options.Add(ProjectOption);
        command.Options.Add(ModelOption);
        command.Options.Add(LanguageOption);
        command.Options.Add(TagOption);
        command.Options.Add(PromptOption);
        command.Options.Add(KeepAudioOption);
        command.Options.Add(ThreadsOption);
        command.Options.Add(DiarizeOption);
        command.Options.Add(NoDiarizeOption);
        command.Options.Add(MaxSpeakersOption);
        command.Options.Add(QuietOption);

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(
                parseResult,
                environment => ExecuteAsync(parseResult, environment, cancellationToken),
                parseResult.GetValue(QuietOption)));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        CliEnvironment environment,
        CancellationToken cancellationToken)
    {
        ConsoleOutput console = environment.Console;

        SessionSupport.ApplyTranscriptionOptions(
            parseResult, environment.Settings, ModelOption, LanguageOption, ThreadsOption, PromptOption);

        SpeakerSupport.ApplyOptions(
            parseResult,
            environment.Settings,
            DiarizeOption,
            NoDiarizeOption,
            MaxSpeakersOption);

        var project = parseResult.GetValue(ProjectOption) ?? environment.Settings.DefaultProject;
        var title = parseResult.GetValue(TitleOption) ?? string.Empty;
        var keepAudio = parseResult.GetValue(KeepAudioOption) || environment.Settings.KeepSessionAudio;
        var requestedChannel = parseResult.GetValue(ChannelOption);

        ModelDownloadReporter reporter = new(console);
        await using WhisperNotesServices services = WhisperNotesServices.Create(environment.Settings, reporter);

        ChannelEntry channel = ResolveChannel(services, requestedChannel, console);

        console.Line();
        await SessionSupport.PrepareModelAsync(services, reporter, console, LabelWidth, cancellationToken)
            .ConfigureAwait(false);

        ISpeakerAttributor? attributor = await SpeakerSupport
            .PrepareAsync(services, console, cancellationToken)
            .ConfigureAwait(false);

        NoteSession session = await services.Notes.CreateSessionAsync(
            title,
            project,
            channel.SourceDescription,
            SessionSupport.Tags(parseResult, TagOption),
            ModelSizes.Name(services.Settings.Model),
            CancellationToken.None).ConfigureAwait(false);

        var sessionDirectory = services.Notes.GetSessionDirectory(session.Id);

        string kindLabel = channel.Channel.Kind switch
        {
            AudioChannelKind.Loopback => "loopback",
            AudioChannelKind.Microphone => "microphone",
            AudioChannelKind.Application when ProcessLoopbackSupport.IsSupported => "application",
            AudioChannelKind.Application => "application → system audio fallback",
            _ => "unknown"
        };

        console.Field("channel", $"{channel.DisplayName} [{kindLabel}]", LabelWidth);
        console.Field("session", sessionDirectory, LabelWidth);
        if (keepAudio)
        {
            console.Field("audio", SessionSupport.AudioPath(services, session.Id), LabelWidth);
        }

        console.Line("  Ctrl+C to stop.");
        console.Line();

        await SessionSupport.RememberChannelAsync(services, channel.Channel.Id, console).ConfigureAwait(false);

        List<NoteEntry> entries;
        AudioCaptureException? failure;

        try
        {
            (entries, failure) = await RunAsync(
                    services,
                    session,
                    channel,
                    keepAudio,
                    console,
                    attributor,
                    cancellationToken)
                .ConfigureAwait(false);

            // Clustering happens after capture so a later return to an earlier voice can reuse the
            // original label instead of inventing a new speaker at every turn.
            await SpeakerSupport
                .AttributeAsync(services, console, session, entries, attributor, FinalLabelWidth)
                .ConfigureAwait(false);
        }
        finally
        {
            attributor?.Dispose();
        }

        // Finalizing is never cancelled: the whole point of the Ctrl+C path is that the notes are
        // written out even though the user asked to stop.
        NoteSession finalized = await services.Notes
            .FinalizeSessionAsync(session.Id, CancellationToken.None)
            .ConfigureAwait(false);

        var notesPath = SessionSupport.NotesPath(services, session.Id);

        if (console.Quiet)
        {
            console.Result(notesPath);
        }
        else
        {
            console.Line();
            console.Field(
                "finalized",
                $"{Format.Human(finalized.Duration)}, {Format.Count(entries.Count, "entry", "entries")}",
                FinalLabelWidth);
            console.Field("notes", notesPath, FinalLabelWidth);
        }

        if (failure is not null)
        {
            console.Error($"capture stopped early: {failure.Message}");
            return ExitCode.DeviceNotFound;
        }

        if (entries.Count == 0)
        {
            console.Warn("no speech was transcribed — check that the chosen endpoint is the one Teams plays through.");
        }

        return cancellationToken.IsCancellationRequested ? ExitCode.Interrupted : ExitCode.Success;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The WAV writer's ownership passes to RecordingCaptureSource, which closes it — "
                        + "and patches the RIFF header before releasing the file handle — from its own "
                        + "DisposeAsync, reached by the 'await using (source)' below. Closing it here "
                        + "would end the recording before the first frame was captured.")]
    private static async Task<(List<NoteEntry> Entries, AudioCaptureException? Failure)> RunAsync(
        WhisperNotesServices services,
        NoteSession session,
        ChannelEntry channel,
        bool keepAudio,
        ConsoleOutput console,
        ISpeakerAttributor? attributor,
        CancellationToken cancellationToken)
    {
        IAudioCaptureSource source = services.CaptureSources.Create(channel.Channel);

        if (keepAudio)
        {
            WavStreamWriter writer = new(
                SessionSupport.AudioPath(services, session.Id),
                AudioFrame.SampleRate,
                AudioFrame.Channels);
            source = new RecordingCaptureSource(source, writer, console);
        }

        TranscriptionOptions options = services.Settings.ToTranscriptionOptions();
        List<NoteEntry> entries = [];

        await using (source.ConfigureAwait(false))
        {
            try
            {
                await foreach (TranscriptSegment segment in services.LiveEngine
                    .RunAsync(source, options, cancellationToken, attributor)
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
                    console.Line($"[{Format.Clock(segment.Start)}] {segment.Text}");
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C arrived before the engine had anything to flush.
            }
            catch (AudioCaptureException ex)
            {
                // Whatever was already appended stays on disk; the caller still finalizes.
                return (entries, ex);
            }
        }

        return (entries, null);
    }

    private static ChannelEntry ResolveChannel(
        WhisperNotesServices services,
        string? requested,
        ConsoleOutput console)
    {
        IReadOnlyList<ChannelEntry> entries = ChannelCatalog.Build(services.Channels);

        if (requested is { Length: > 0 })
        {
            return ChannelCatalog.Resolve(entries, requested)
                   ?? throw SessionSupport.UnknownChannel(requested, entries);
        }

        if (services.Settings.LastChannelId is { Length: > 0 } remembered &&
            ChannelCatalog.Resolve(entries, remembered) is { } previous)
        {
            console.Diagnostic($"reusing the last channel {previous.Slug}");
            return previous;
        }

        return ChannelCatalog.PreferredDefault(entries)
               ?? throw new CliException(
                   ExitCode.DeviceNotFound,
                   "No active audio endpoints were found. Check that an audio device is enabled in Windows sound settings.");
    }
}

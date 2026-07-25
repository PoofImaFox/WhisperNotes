using System.CommandLine;
using NoteScribe.Cli.Audio;
using NoteScribe.Cli.Rendering;
using NoteScribe.Core.Audio;
using NoteScribe.Core.Composition;
using NoteScribe.Core.Notes;
using NoteScribe.Core.Transcription;

namespace NoteScribe.Cli.Commands;

/// <summary>
/// Live capture and dictation. Runs until Ctrl+C, then finalizes the session and writes notes.md.
/// </summary>
internal static class ListenCommand
{
    private const int LabelWidth = 9;
    private const int FinalLabelWidth = 11;

    private static readonly Option<string> ChannelOption = new("--channel", "-c")
    {
        Description = "Endpoint from 'notescribe devices'. Defaults to the last used, else the default render loopback.",
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

        var project = parseResult.GetValue(ProjectOption) ?? environment.Settings.DefaultProject;
        var title = parseResult.GetValue(TitleOption) ?? string.Empty;
        var keepAudio = parseResult.GetValue(KeepAudioOption) || environment.Settings.KeepSessionAudio;
        var requestedChannel = parseResult.GetValue(ChannelOption);

        ModelDownloadReporter reporter = new(console);
        await using NoteScribeServices services = NoteScribeServices.Create(environment.Settings, reporter);

        ChannelEntry channel = ResolveChannel(services, requestedChannel, console);

        console.Line();
        await SessionSupport.PrepareModelAsync(services, reporter, console, LabelWidth, cancellationToken)
            .ConfigureAwait(false);

        NoteSession session = await services.Notes.CreateSessionAsync(
            title,
            project,
            channel.SourceDescription,
            SessionSupport.Tags(parseResult, TagOption),
            ModelSizes.Name(services.Settings.Model),
            CancellationToken.None).ConfigureAwait(false);

        var sessionDirectory = services.Notes.GetSessionDirectory(session.Id);

        console.Field("channel", $"{channel.DisplayName} [{(channel.IsLoopback ? "loopback" : "microphone")}]", LabelWidth);
        console.Field("session", sessionDirectory, LabelWidth);
        if (keepAudio)
        {
            console.Field("audio", SessionSupport.AudioPath(services, session.Id), LabelWidth);
        }

        console.Line("  Ctrl+C to stop.");
        console.Line();

        await SessionSupport.RememberChannelAsync(services, channel.Channel.Id, console).ConfigureAwait(false);

        var (entries, failure) = await RunAsync(services, session, channel, keepAudio, console, cancellationToken)
            .ConfigureAwait(false);

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
                $"{Format.Human(finalized.Duration)}, {Format.Count(entries, "entry", "entries")}",
                FinalLabelWidth);
            console.Field("notes", notesPath, FinalLabelWidth);
        }

        if (failure is not null)
        {
            console.Error($"capture stopped early: {failure.Message}");
            return ExitCode.DeviceNotFound;
        }

        if (entries == 0)
        {
            console.Warn("no speech was transcribed — check that the chosen endpoint is the one Teams plays through.");
        }

        return cancellationToken.IsCancellationRequested ? ExitCode.Interrupted : ExitCode.Success;
    }

    private static async Task<(int Entries, AudioCaptureException? Failure)> RunAsync(
        NoteScribeServices services,
        NoteSession session,
        ChannelEntry channel,
        bool keepAudio,
        ConsoleOutput console,
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
        var entries = 0;

        await using (source.ConfigureAwait(false))
        {
            try
            {
                await foreach (TranscriptSegment segment in services.LiveEngine
                    .RunAsync(source, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(segment.Text))
                    {
                        continue;
                    }

                    await services.Notes
                        .AppendEntryAsync(session.Id, SessionSupport.ToEntry(session, segment), CancellationToken.None)
                        .ConfigureAwait(false);

                    entries++;
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
        NoteScribeServices services,
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

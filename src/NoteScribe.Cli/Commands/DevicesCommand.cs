using System.CommandLine;
using System.Globalization;
using NoteScribe.Cli.Audio;
using NoteScribe.Core.Audio;
using NoteScribe.Core.Composition;

namespace NoteScribe.Cli.Commands;

/// <summary>Lists the capturable endpoints together with the ids <c>--channel</c> accepts.</summary>
internal static class DevicesCommand
{
    public static Command Create()
    {
        Command command = new("devices", "List the audio endpoints you can capture, with the ids to pass to --channel.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, async environment =>
            {
                await using NoteScribeServices services = NoteScribeServices.Create(environment.Settings);
                IReadOnlyList<ChannelEntry> entries = ChannelCatalog.Build(services.Channels);

                environment.Console.Result();
                Section(environment, entries, AudioChannelKind.Loopback,
                    "LOOPBACK (system audio — capture what you HEAR, e.g. Teams)");
                environment.Console.Result();
                Section(environment, entries, AudioChannelKind.Microphone,
                    "MICROPHONE (capture what you SAY)");
                environment.Console.Result();
                environment.Console.Result("  * = system default for that role");

                return ExitCode.Success;
            }));

        return command;
    }

    private static void Section(
        CliEnvironment environment,
        IReadOnlyList<ChannelEntry> entries,
        AudioChannelKind kind,
        string heading)
    {
        environment.Console.Result(heading);

        List<ChannelEntry> matching = [.. entries.Where(e => e.Channel.Kind == kind)];
        if (matching.Count == 0)
        {
            environment.Console.Result("    (none found)");
            return;
        }

        var slugWidth = matching.Max(e => e.Slug.Length);
        var nameWidth = matching.Max(e => e.DisplayName.Length);

        foreach (ChannelEntry entry in matching)
        {
            var marker = entry.Channel.IsDefault ? "  * " : "    ";
            var format = entry.Channel.NativeSampleRate > 0
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} Hz / {1}ch",
                    entry.Channel.NativeSampleRate,
                    entry.Channel.NativeChannels)
                : "format unavailable";

            var line = marker
                       + entry.Slug.PadRight(slugWidth + 4)
                       + entry.DisplayName.PadRight(nameWidth + 3)
                       + format;

            if (entry.Channel.IsDefault)
            {
                line = line.PadRight(line.Length + Math.Max(0, 20 - format.Length)) + "[default]";
            }

            environment.Console.Result(line.TrimEnd());
            environment.Console.Diagnostic($"endpoint id {entry.Channel.Id}");
        }
    }
}

using System.CommandLine;
using System.Globalization;
using WhisperNotes.Cli.Audio;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Composition;

namespace WhisperNotes.Cli.Commands;

/// <summary>Lists the capturable endpoints together with the ids <c>--channel</c> accepts.</summary>
internal static class DevicesCommand
{
    public static Command Create()
    {
        Command command = new("devices", "List the audio endpoints you can capture, with the ids to pass to --channel.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, async environment =>
            {
                await using WhisperNotesServices services = WhisperNotesServices.Create(environment.Settings);
                IReadOnlyList<ChannelEntry> entries = ChannelCatalog.Build(services.Channels);

                environment.Console.Result();
                Section(environment, entries, AudioChannelKind.Loopback,
                    "LOOPBACK (system audio — capture what you HEAR, e.g. Teams)");
                environment.Console.Result();
                Section(environment, entries, AudioChannelKind.Microphone,
                    "MICROPHONE (capture what you SAY)");
                environment.Console.Result();

                // The reason is null on a machine that supports process loopback, so on Windows 11 /
                // Server 2022 this section reads exactly like the two above.
                Section(environment, entries, AudioChannelKind.Application,
                    "APPLICATIONS (capture ONE app's audio — nothing else on the machine)",
                    ProcessLoopbackSupport.UnsupportedReason);
                environment.Console.Result();
                environment.Console.Result("  * = system default for that role");

                return ExitCode.Success;
            }));

        return command;
    }

    /// <param name="caveat">
    /// Printed under the heading before the rows, for a section whose entries do not do what their
    /// heading promises. This is how the applications list admits that below Windows build
    /// <see cref="ProcessLoopbackSupport.MinimumBuild"/> it degrades to whole-machine audio: listing
    /// the apps in silence would let a user record a "just Teams" session that quietly captured
    /// their music too.
    /// </param>
    private static void Section(
        CliEnvironment environment,
        IReadOnlyList<ChannelEntry> entries,
        AudioChannelKind kind,
        string heading,
        string? caveat = null)
    {
        environment.Console.Result(heading);

        if (caveat is { Length: > 0 })
        {
            environment.Console.Result("  ! " + caveat);
        }

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

            // The pid is not part of the id (it is recycled between runs), so -v is the only place
            // it can be seen — and it is the thing you need to tell two windows of one app apart.
            environment.Console.Diagnostic(entry.IsApplication
                ? $"endpoint id {entry.Channel.Id} (pid {entry.Channel.ProcessId.ToString(CultureInfo.InvariantCulture)})"
                : $"endpoint id {entry.Channel.Id}");
        }
    }
}

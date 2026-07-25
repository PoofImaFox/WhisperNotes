using System.CommandLine;
using System.Text;
using NoteScribe.Cli.Commands;
using NoteScribe.Cli.Interop;

namespace NoteScribe.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        EnableUnicodeOutput();

        RootCommand root = new(
            "Local speech-to-text note taking. Captures a Windows audio endpoint or a recorded file "
            + "and writes timestamped notes. Nothing leaves the machine.");

        CliOptions.AddGlobals(root);

        root.Subcommands.Add(DevicesCommand.Create());
        root.Subcommands.Add(ListenCommand.Create());
        root.Subcommands.Add(TranscribeCommand.Create());
        root.Subcommands.Add(SessionsCommand.Create());
        root.Subcommands.Add(ModelsCommand.Create());
        root.Subcommands.Add(ConfigCommand.Create());

        InvocationConfiguration configuration = new()
        {
            // Ctrl+C is ours. The library's own handler would hard-kill the process two seconds
            // after the first press, which is exactly long enough to lose the final flush of a
            // meeting: 'listen' needs however long whisper takes to decode the buffered tail.
            ProcessTerminationTimeout = null
        };

        using InterruptSignal interrupt = new();

        ParseResult parseResult = root.Parse(args);
        return await parseResult.InvokeAsync(configuration, interrupt.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// The progress bars and device names use characters the legacy OEM code page cannot render.
    /// A console that refuses the switch just gets the code page it had.
    /// </summary>
    private static void EnableUnicodeOutput()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or PlatformNotSupportedException)
        {
        }
    }
}

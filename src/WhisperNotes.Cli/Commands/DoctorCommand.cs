using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Composition;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Cli.Commands;

/// <summary>
/// Answers "is this actually using my graphics card?". Whisper.net decides that at load time from
/// what the machine supports, silently falling back to the CPU, and a CPU fallback looks exactly
/// like a GPU run except roughly 40x slower. The only honest way to report it is to load a model
/// and say what came back.
/// </summary>
internal static class DoctorCommand
{
    private const int LabelWidth = 11;

    public static Command Create()
    {
        Command command = new("doctor", "Report which backend transcription runs on, and on which device.");

        command.SetAction((ParseResult parseResult, CancellationToken cancellationToken) =>
            CliExecutor.RunAsync(parseResult, environment => ExecuteAsync(environment, cancellationToken)));

        return command;
    }

    private static async Task<int> ExecuteAsync(CliEnvironment environment, CancellationToken cancellationToken)
    {
        ConsoleOutput console = environment.Console;
        await using WhisperNotesServices services = WhisperNotesServices.Create(environment.Settings);

        WhisperModelSize size = ResolveModel(services);
        console.Line();
        console.Field("model", ModelSizes.Name(size), LabelWidth);

        TranscriptionOptions options = services.Settings.ToTranscriptionOptions() with { Model = size };
        console.Field("requested", options.UseGpu ? $"gpu, device {Index(options.GpuDevice)}" : "cpu", LabelWidth);

        Stopwatch load = Stopwatch.StartNew();
        await using (await services.Transcribers.CreateAsync(options, cancellationToken).ConfigureAwait(false))
        {
            load.Stop();
        }

        Report(console, options);
        console.Field("load", $"{load.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s", LabelWidth);
        console.Line();

        return ExitCode.Success;
    }

    private static void Report(ConsoleOutput console, TranscriptionOptions options)
    {
        WhisperBackend backend = WhisperRuntime.LoadedBackend;
        console.Field(
            "backend",
            backend switch
            {
                WhisperBackend.Unresolved => "unresolved — the native library reported nothing",
                WhisperBackend.Cpu => "cpu — NOT gpu accelerated",

                // Deliberately not claimed as accelerated: this is a backend we neither ship nor
                // have measured, so the only honest thing to report is which one it is.
                WhisperBackend.Other => "a runtime this build does not ship — speed unknown",
                _ => $"{backend.ToString().ToLowerInvariant()} — gpu accelerated"
            },
            LabelWidth);

        IReadOnlyList<WhisperDevice> devices = WhisperRuntime.DeviceReport;
        for (var i = 0; i < devices.Count; i++)
        {
            console.Field(
                i == 0 ? "devices" : string.Empty,
                $"{Index(devices[i].Index)} = {devices[i].Description}",
                LabelWidth);
        }

        if (devices.Count > 1)
        {
            console.Line();
            console.Line(
                $"  More than one adapter is visible and device {Index(options.GpuDevice)} is in use. Try the others"
                + " with --gpu-device <n>, and make the winner stick by setting Gpu.Device in the settings file.");
        }

        if (backend is WhisperBackend.Cpu)
        {
            WarnAboutCpuFallback(console, options);
        }
    }

    private static void WarnAboutCpuFallback(ConsoleOutput console, TranscriptionOptions options)
    {
        console.Line();

        if (!options.UseGpu)
        {
            console.Line("  The GPU was not asked for — either --no-gpu, or Gpu.Enabled is false in the settings file.");
            return;
        }

        console.Warn(
            "transcription is running on the CPU, which is roughly 40x slower than the GPU path.");
        console.Line(
            "  The Vulkan runtime needs vulkan-1.dll, which current NVIDIA, AMD and Intel drivers all"
            + " install. Updating the graphics driver is the usual fix. Re-run with --verbose to see"
            + " every path the loader tried.");
    }

    /// <summary>
    /// Falls back to any downloaded model rather than pulling gigabytes just to answer a question
    /// about hardware — the backend that gets loaded is the same whichever weights sit on top of it.
    /// </summary>
    private static WhisperModelSize ResolveModel(WhisperNotesServices services)
    {
        if (services.Models.IsDownloaded(services.Settings.Model))
        {
            return services.Settings.Model;
        }

        foreach (WhisperModelSize candidate in ModelSizes.All)
        {
            if (services.Models.IsDownloaded(candidate))
            {
                return candidate;
            }
        }

        throw new CliException(
            ExitCode.ModelUnavailable,
            "No whisper weights are downloaded yet, and doctor has to load one to find out which "
            + "backend it lands on. Run 'whispernotes models download tiny' first.");
    }

    private static string Index(int value) => value.ToString(CultureInfo.InvariantCulture);
}

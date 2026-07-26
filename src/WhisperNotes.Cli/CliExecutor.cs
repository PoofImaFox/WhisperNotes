using System.CommandLine;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Media;

namespace WhisperNotes.Cli;

/// <param name="Settings">Settings file merged with the global command line overrides.</param>
/// <param name="SettingsPath">Where those settings were loaded from.</param>
internal sealed record CliEnvironment(AppSettings Settings, string SettingsPath, ConsoleOutput Console);

/// <summary>
/// Loads settings, applies the global overrides, and converts anything that escapes a command into
/// one sentence plus a documented exit code. No command is allowed to surface a stack trace.
/// </summary>
internal static class CliExecutor
{
    public static async Task<int> RunAsync(
        ParseResult parseResult,
        Func<CliEnvironment, Task<int>> body,
        bool quiet = false)
    {
        var console = new ConsoleOutput(parseResult.GetValue(CliOptions.Verbose), quiet);

        CliEnvironment environment;
        try
        {
            environment = BuildEnvironment(parseResult, console);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            console.Error(ex.Message);
            return ExitCode.Usage;
        }

        try
        {
            return await body(environment).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            console.Error(ex.Message);
            Diagnose(console, ex);
            return ex.ExitCode;
        }
        catch (MediaConversionException ex)
        {
            console.Error(ex.Message);
            Diagnose(console, ex);
            return ExitCode.MediaFailed;
        }
        catch (AudioCaptureException ex)
        {
            console.Error(ex.Message);
            Diagnose(console, ex);
            return ExitCode.DeviceNotFound;
        }
        catch (OperationCanceledException)
        {
            return ExitCode.Interrupted;
        }
        catch (HttpRequestException ex)
        {
            console.Error($"Network request failed: {ex.Message}");
            Diagnose(console, ex);
            return ExitCode.ModelUnavailable;
        }
        catch (UnauthorizedAccessException ex)
        {
            console.Error($"Access denied: {ex.Message}");
            Diagnose(console, ex);
            return ExitCode.Usage;
        }
        catch (Exception ex)
        {
            console.Error(ex.Message);
            Diagnose(console, ex);
            return ExitCode.Usage;
        }
    }

    private static CliEnvironment BuildEnvironment(ParseResult parseResult, ConsoleOutput console)
    {
        var store = new JsonSettingsStore();
        AppSettings settings = store.Load();

        // Command line beats the settings file, which beats the built-in defaults already baked
        // into AppSettings.
        if (parseResult.GetValue(CliOptions.NotesRoot) is { Length: > 0 } notesRoot)
        {
            settings.NotesRoot = Path.GetFullPath(notesRoot);
        }

        if (parseResult.GetValue(CliOptions.ModelsRoot) is { Length: > 0 } modelsRoot)
        {
            settings.ModelsRoot = Path.GetFullPath(modelsRoot);
        }

        if (parseResult.GetValue(CliOptions.Ffmpeg) is { Length: > 0 } ffmpeg)
        {
            settings.FfmpegPath = ffmpeg;
        }

        if (parseResult.GetValue(CliOptions.NoGpu))
        {
            settings.Gpu.Enabled = false;
        }

        if (parseResult.GetValue(CliOptions.GpuDevice) is { } gpuDevice)
        {
            if (gpuDevice < 0)
            {
                throw new ArgumentException("--gpu-device is an index into the adapter list, so it cannot be negative.");
            }

            settings.Gpu.Device = gpuDevice;
        }

        console.Diagnostic($"settings   {store.SettingsPath}");
        console.Diagnostic($"notes root {settings.NotesRoot}");
        console.Diagnostic($"models     {settings.ModelsRoot}");

        return new CliEnvironment(settings, store.SettingsPath, console);
    }

    private static void Diagnose(ConsoleOutput console, Exception ex)
    {
        if (console.Verbose)
        {
            console.Diagnostic(ex.ToString());
        }
    }
}

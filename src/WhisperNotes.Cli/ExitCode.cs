namespace WhisperNotes.Cli;

/// <summary>The exit codes documented in <c>docs/CLI.md</c>.</summary>
internal static class ExitCode
{
    public const int Success = 0;

    /// <summary>Bad usage or an invalid argument.</summary>
    public const int Usage = 1;

    /// <summary>The requested audio device was not found, or died mid-session.</summary>
    public const int DeviceNotFound = 2;

    /// <summary>ffmpeg is missing, or the conversion failed.</summary>
    public const int MediaFailed = 3;

    /// <summary>The model is missing and could not be downloaded.</summary>
    public const int ModelUnavailable = 4;

    /// <summary>Interrupted with Ctrl+C. For <c>listen</c> this is the normal exit.</summary>
    public const int Interrupted = 130;
}

/// <summary>An error we can report as a sentence plus a documented exit code.</summary>
internal sealed class CliException : Exception
{
    public CliException(int exitCode, string message, Exception? inner = null)
        : base(message, inner) => ExitCode = exitCode;

    public int ExitCode { get; }
}

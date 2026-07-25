using NoteScribe.Core.Transcription;

namespace NoteScribe.Cli.Rendering;

/// <summary>
/// Turns whisper weight downloads into a visible bar.
/// </summary>
/// <remarks>
/// This is handed to <c>NoteScribeServices.Create</c> once, before we know whether a download will
/// happen at all, so it arms itself on the first report and stands down when the transfer finishes.
/// A 1.5 GB fetch starting silently at the moment a meeting begins is the failure this prevents.
/// </remarks>
internal sealed class ModelDownloadReporter : IProgress<ModelDownloadProgress>
{
    private readonly ConsoleOutput _console;
    private readonly Lock _gate = new();

    private ProgressIndicator? _indicator;

    public ModelDownloadReporter(ConsoleOutput console) => _console = console;

    public void Report(ModelDownloadProgress value)
    {
        lock (_gate)
        {
            _indicator ??= new ProgressIndicator(_console, "downloading");

            var detail = value.TotalBytes is { } total
                ? $"{Format.Bytes(value.BytesRead)} / {Format.Bytes(total)}"
                : Format.Bytes(value.BytesRead);

            if (value.TotalBytes is { } expected && expected > 0 && value.BytesRead >= expected)
            {
                _indicator.Complete(1, detail);
                _indicator = null;
                return;
            }

            _indicator.Report(value.Fraction, detail);
        }
    }

    /// <summary>Closes any open bar — call before printing anything else.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            _indicator?.Complete();
            _indicator = null;
        }
    }

    /// <summary>Clears a partially drawn bar after a failed download.</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _indicator?.Abandon();
            _indicator = null;
        }
    }
}

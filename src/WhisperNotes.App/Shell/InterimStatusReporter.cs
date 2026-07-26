using System.Globalization;
using Avalonia.Threading;
using WhisperNotes.App.ViewModels;

namespace WhisperNotes.App.Shell;

/// <summary>
/// Owns the transcript's interim line — the one that tells the user whether the pipeline is alive
/// in the gaps between committed segments.
/// </summary>
/// <remarks>
/// The engine contract only yields committed segments, so there is no partial text to show. This
/// reports pipeline state instead (audio arriving, nothing decoded yet) rather than inventing it,
/// which is why it is timer-driven rather than event-driven: the interesting case is precisely
/// when nothing has happened for a while.
/// </remarks>
internal sealed class InterimStatusReporter
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(300);

    private readonly SessionDocumentViewModel _document;
    private readonly AudioMeterViewModel _meter;
    private readonly IRecordingShell _shell;
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _lastSegmentAt;

    public InterimStatusReporter(
        SessionDocumentViewModel document,
        AudioMeterViewModel meter,
        IRecordingShell shell)
    {
        _document = document;
        _meter = meter;
        _shell = shell;
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick);
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>Restarts the silence clock: a session just began, or a segment just landed.</summary>
    public void MarkActivity() => _lastSegmentAt = DateTimeOffset.Now;

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_shell.IsRecording)
        {
            _document.InterimText = null;
            return;
        }

        var since = DateTimeOffset.Now - _lastSegmentAt;

        _document.InterimText = (_meter.HasSignal, since.TotalSeconds) switch
        {
            (true, > 0.6) => string.Create(CultureInfo.CurrentCulture, $"decoding… {since.TotalSeconds:0.0}s of speech buffered"),
            (false, > 3.0) => "listening — no audio on the enabled inputs yet",
            _ => null
        };
    }
}

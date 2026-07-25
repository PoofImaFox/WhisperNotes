using System.Diagnostics;

namespace NoteScribe.Cli.Rendering;

/// <summary>
/// A one-line progress bar that also behaves when stdout is a file.
/// </summary>
/// <remarks>
/// Redirected output gets periodic complete lines instead of carriage-return redraws: a log full of
/// half-overwritten bars is worse than no bar at all, and this tool is routinely run from a script.
/// No ANSI escapes are emitted in either mode, only '\r'.
/// </remarks>
internal sealed class ProgressIndicator : IDisposable
{
    private const int DefaultBarWidth = 22;
    private const int RedirectedPercentStep = 10;
    private const char Filled = '█';
    private const char Empty = '░';

    private static readonly TimeSpan InteractiveInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RedirectedInterval = TimeSpan.FromSeconds(5);

    private readonly ConsoleOutput _console;
    private readonly string _label;
    private readonly int _labelWidth;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    private TimeSpan? _lastRender;
    private int _lastPercent = -1;
    private int _lastLineLength;
    private int _marquee;
    private bool _finished;
    private string? _lastText;

    public ProgressIndicator(ConsoleOutput console, string label, int labelWidth = 13)
    {
        _console = console;
        _label = label;
        _labelWidth = labelWidth;
    }

    public void Report(double? fraction, string? detail = null)
    {
        if (_console.Quiet)
        {
            return;
        }

        lock (_gate)
        {
            if (_finished || !ShouldRender(fraction))
            {
                return;
            }

            Render(fraction, detail, newLine: !_console.Interactive);
        }
    }

    /// <summary>Draws the bar one last time at its final value and closes the line.</summary>
    public void Complete(double? fraction = 1, string? detail = null)
    {
        if (_console.Quiet)
        {
            return;
        }

        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            Render(fraction, detail, newLine: true);
        }
    }

    /// <summary>Abandons the line without claiming completion — used when the operation failed.</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            if (!_console.Quiet && _console.Interactive && _lastLineLength > 0)
            {
                _console.Write("\r" + new string(' ', _lastLineLength) + "\r");
            }
        }
    }

    public void Dispose() => Complete();

    private bool ShouldRender(double? fraction)
    {
        TimeSpan now = _clock.Elapsed;

        if (_console.Interactive)
        {
            if (_lastRender is { } drawn && now - drawn < InteractiveInterval)
            {
                return false;
            }

            _lastRender = now;
            return true;
        }

        var percent = fraction is { } f ? (int)Math.Round(Math.Clamp(f, 0, 1) * 100) : -1;
        var steppedPast = percent >= 0 && percent / RedirectedPercentStep > Math.Max(_lastPercent, 0) / RedirectedPercentStep;

        if (_lastRender is { } last && !steppedPast && now - last < RedirectedInterval)
        {
            return false;
        }

        _lastRender = now;
        _lastPercent = percent;
        return true;
    }

    private void Render(double? fraction, string? detail, bool newLine)
    {
        var text = "  " + _label.PadRight(_labelWidth) + " " + Bar(fraction) + " " + PercentText(fraction);
        if (!string.IsNullOrEmpty(detail))
        {
            text += "   " + detail;
        }

        // Redirected output has no line to overwrite, so an unchanged final line would just be a
        // duplicate of the last periodic one.
        if (!_console.Interactive && string.Equals(text, _lastText, StringComparison.Ordinal))
        {
            return;
        }

        _lastText = text;

        if (newLine)
        {
            // Interactive mode has a partially drawn line to overwrite before the newline.
            _console.Write(_console.Interactive ? "\r" + text.PadRight(_lastLineLength) + Environment.NewLine : text + Environment.NewLine);
            _lastLineLength = 0;
            return;
        }

        _console.Write("\r" + text.PadRight(_lastLineLength));
        _lastLineLength = text.Length;
    }

    private string Bar(double? fraction)
    {
        var width = BarWidth();

        if (fraction is not { } value)
        {
            // Unknown total (a server that omits Content-Length). A moving block still proves
            // the transfer is alive, which is the whole point of showing anything.
            var block = Math.Min(4, width);
            var position = _marquee++ % Math.Max(1, width - block + 1);
            return new string(Empty, position) + new string(Filled, block) + new string(Empty, width - block - position);
        }

        var filled = (int)Math.Round(Math.Clamp(value, 0, 1) * width);
        return new string(Filled, filled) + new string(Empty, width - filled);
    }

    private static string PercentText(double? fraction) =>
        fraction is { } value ? Format.Percent(value).PadLeft(4) : "    ";

    private static int BarWidth()
    {
        try
        {
            // Leave room for the label, the percentage and a detail suffix.
            return Math.Clamp(Console.WindowWidth - 48, 10, DefaultBarWidth);
        }
        catch (IOException)
        {
            return DefaultBarWidth;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DefaultBarWidth;
        }
    }
}

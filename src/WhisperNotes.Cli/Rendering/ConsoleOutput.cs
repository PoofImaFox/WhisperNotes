namespace WhisperNotes.Cli.Rendering;

/// <summary>
/// Every byte the CLI prints goes through here so <c>--quiet</c> and <c>--verbose</c> are honoured
/// in one place, and so the progress renderer can ask once whether it is talking to a real console.
/// </summary>
internal sealed class ConsoleOutput
{
    public ConsoleOutput(bool verbose, bool quiet)
    {
        Verbose = verbose;
        Quiet = quiet;
        Interactive = !Console.IsOutputRedirected;
    }

    public bool Verbose { get; }

    public bool Quiet { get; }

    /// <summary>False when stdout is a file or a pipe — no carriage-return redraws in that case.</summary>
    public bool Interactive { get; }

    /// <summary>Output the command exists to produce. Never suppressed.</summary>
    public void Result(string text = "") => Console.Out.WriteLine(text);

    /// <summary>Progress chatter around the result. Suppressed by <c>--quiet</c>.</summary>
    public void Line(string text = "")
    {
        if (!Quiet)
        {
            Console.Out.WriteLine(text);
        }
    }

    public void Write(string text)
    {
        if (!Quiet)
        {
            Console.Out.Write(text);
        }
    }

    /// <summary>A <c>  label   value</c> row, matching the layout in docs/CLI.md.</summary>
    public void Field(string label, string value, int labelWidth = 9) =>
        Line("  " + label.PadRight(labelWidth) + " " + value);

    public void Diagnostic(string text)
    {
        if (Verbose)
        {
            Console.Error.WriteLine("  · " + text);
        }
    }

    public void Warn(string text) => Console.Error.WriteLine("warning: " + text);

    public void Error(string text) => Console.Error.WriteLine("error: " + text);
}

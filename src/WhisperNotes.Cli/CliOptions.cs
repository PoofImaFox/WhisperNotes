using System.CommandLine;

namespace WhisperNotes.Cli;

/// <summary>
/// The options shared by more than one command. Definitions live here rather than inside each
/// command so <c>listen</c> and <c>transcribe</c> cannot drift apart on names or defaults.
/// </summary>
internal static class CliOptions
{
    public static Option<string> NotesRoot { get; } = new("--notes-root")
    {
        Description = "Override the notes root for this invocation.",
        HelpName = "dir",
        Recursive = true
    };

    public static Option<string> ModelsRoot { get; } = new("--models-root")
    {
        Description = "Override the models directory.",
        HelpName = "dir",
        Recursive = true
    };

    public static Option<string> Ffmpeg { get; } = new("--ffmpeg")
    {
        Description = "Explicit ffmpeg location if it isn't on PATH.",
        HelpName = "path",
        Recursive = true
    };

    public static Option<bool> Verbose { get; } = new("--verbose")
    {
        Description = "Diagnostic logging, including resolved binary paths.",
        Recursive = true
    };

    public static Option<string> Title() => new("--title", "-t")
    {
        Description = "Session title; also the folder name.",
        HelpName = "text"
    };

    public static Option<string> Project() => new("--project", "-p")
    {
        Description = "Groups the session into a project folder. Defaults to the configured project.",
        HelpName = "name"
    };

    public static Option<string> Model() => new Option<string>("--model", "-m")
    {
        Description = "Whisper weights to run: " + string.Join(" | ", ModelSizes.Names) + ".",
        HelpName = "size"
    }.AcceptOnlyFromAmong(ModelSizes.Names);

    public static Option<string> Language() => new("--language", "-l")
    {
        Description = "ISO language code, or 'auto' to detect.",
        HelpName = "code"
    };

    public static Option<string[]> Tag() => new("--tag")
    {
        Description = "Tag written into the notes front matter. Repeatable.",
        HelpName = "tag",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false
    };

    public static Option<string> Prompt() => new("--prompt")
    {
        Description = "Vocabulary hint — client names, acronyms.",
        HelpName = "text"
    };

    public static Option<bool> KeepAudio() => new("--keep-audio")
    {
        Description = "Save the WAV next to the notes instead of discarding it."
    };

    public static Option<int?> Threads() => new("--threads")
    {
        Description = "Decoder threads. Defaults to the CPU count, capped.",
        HelpName = "n"
    };

    public static Option<bool> Diarize() => new("--diarize")
    {
        Description = "Label each line with the speaker who said it."
    };

    public static Option<bool> NoDiarize() => new("--no-diarize")
    {
        Description = "Skip speaker attribution, even if settings enable it."
    };

    public static Option<int?> MaxSpeakers() => new("--max-speakers")
    {
        Description = "Ceiling on how many voices may be reported.",
        HelpName = "n"
    };

    /// <summary>Adds the options every command inherits.</summary>
    public static void AddGlobals(RootCommand root)
    {
        root.Options.Add(NotesRoot);
        root.Options.Add(ModelsRoot);
        root.Options.Add(Ffmpeg);
        root.Options.Add(Verbose);
    }
}

using NoteScribe.Core.Transcription;

namespace NoteScribe.Cli;

/// <summary>Maps the model names used on the command line to <see cref="WhisperModelSize"/>.</summary>
internal static class ModelSizes
{
    /// <summary>Ordered smallest first — the order <c>models list</c> prints.</summary>
    public static readonly WhisperModelSize[] All =
    [
        WhisperModelSize.Tiny,
        WhisperModelSize.Base,
        WhisperModelSize.Small,
        WhisperModelSize.Medium,
        WhisperModelSize.LargeV3,
        WhisperModelSize.LargeV3Turbo
    ];

    public static string[] Names { get; } = [.. All.Select(Name)];

    public static string Name(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Tiny => "tiny",
        WhisperModelSize.Base => "base",
        WhisperModelSize.Small => "small",
        WhisperModelSize.Medium => "medium",
        WhisperModelSize.LargeV3 => "large-v3",
        WhisperModelSize.LargeV3Turbo => "large-v3-turbo",
        _ => size.ToString().ToLowerInvariant()
    };

    public static bool TryParse(string? value, out WhisperModelSize size)
    {
        foreach (WhisperModelSize candidate in All)
        {
            if (string.Equals(Name(candidate), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                size = candidate;
                return true;
            }
        }

        size = WhisperModelSize.Base;
        return false;
    }

    public static WhisperModelSize Parse(string? value) =>
        TryParse(value, out WhisperModelSize size)
            ? size
            : throw new CliException(
                ExitCode.Usage,
                $"Unknown model '{value}'. Valid models: {string.Join(", ", Names)}.");
}

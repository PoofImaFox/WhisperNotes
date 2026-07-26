using System.CommandLine;
using WhisperNotes.Cli.Audio;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Composition;
using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Notes;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Cli.Commands;

/// <summary>Pieces <c>listen</c> and <c>transcribe</c> both need, kept identical between them.</summary>
internal static class SessionSupport
{
    /// <summary>Applies the shared capture/decode options onto the merged settings.</summary>
    public static void ApplyTranscriptionOptions(
        ParseResult parseResult,
        AppSettings settings,
        Option<string> model,
        Option<string> language,
        Option<int?> threads,
        Option<string> prompt)
    {
        if (parseResult.GetValue(model) is { Length: > 0 } modelName)
        {
            settings.Model = ModelSizes.Parse(modelName);
        }

        if (parseResult.GetValue(language) is { Length: > 0 } languageCode)
        {
            settings.Language = languageCode.Trim();
        }

        if (parseResult.GetValue(threads) is { } threadCount)
        {
            if (threadCount < 1)
            {
                throw new CliException(ExitCode.Usage, "--threads must be at least 1.");
            }

            settings.Threads = threadCount;
        }

        if (parseResult.GetValue(prompt) is { Length: > 0 } hint)
        {
            settings.InitialPrompt = hint;
        }
    }

    /// <summary>
    /// Fetches the weights before anything else starts, so the wait is visible and attributable
    /// rather than an unexplained pause once a meeting is already running.
    /// </summary>
    public static async Task PrepareModelAsync(
        WhisperNotesServices services,
        ModelDownloadReporter reporter,
        ConsoleOutput console,
        int labelWidth,
        CancellationToken cancellationToken)
    {
        WhisperModelSize size = services.Settings.Model;
        var downloaded = services.Models.IsDownloaded(size);

        console.Field(
            "model",
            $"{ModelSizes.Name(size)} ({(downloaded ? "already downloaded" : "not downloaded yet")})",
            labelWidth);

        if (downloaded)
        {
            return;
        }

        try
        {
            await services.Models.EnsureDownloadedAsync(size, reporter, cancellationToken).ConfigureAwait(false);
            reporter.Finish();
        }
        catch (OperationCanceledException)
        {
            reporter.Abandon();
            throw;
        }
        catch (Exception ex)
        {
            reporter.Abandon();
            throw new CliException(
                ExitCode.ModelUnavailable,
                $"Could not download the '{ModelSizes.Name(size)}' weights into {services.Settings.ModelsRoot}: {ex.Message}",
                ex);
        }
    }

    public static NoteEntry ToEntry(NoteSession session, TranscriptSegment segment) => new(
        Guid.CreateVersion7().ToString("n"),
        session.StartedUtc + segment.Start,
        segment.Start,
        NoteEntryKind.Dictation,
        segment.Text,
        // Left unset here even when diarization is running. Which voice this is cannot be known
        // until the whole recording has been heard, so the line is written now — it is the part
        // that matters if the process dies — and attributed afterwards.
        Speaker: null,
        segment.Confidence,
        segment.End);

    public static string NotesPath(WhisperNotesServices services, string sessionId) =>
        Path.Combine(services.Notes.GetSessionDirectory(sessionId), FileSystemNoteRepository.NotesFileName);

    public static string AudioPath(WhisperNotesServices services, string sessionId) =>
        Path.Combine(
            services.Notes.GetSessionDirectory(sessionId),
            FileSystemNoteRepository.AudioDirectoryName,
            "session.wav");

    public static string[] Tags(ParseResult parseResult, Option<string[]> option) =>
        [.. (parseResult.GetValue(option) ?? []).Where(static t => !string.IsNullOrWhiteSpace(t)).Select(static t => t.Trim())];

    /// <summary>
    /// Records which endpoint was used so the next <c>listen</c> reopens on it. Failing to persist a
    /// preference must never cost the user a session, so this only warns.
    /// </summary>
    public static async Task RememberChannelAsync(
        WhisperNotesServices services,
        string channelId,
        ConsoleOutput console)
    {
        try
        {
            // Reloaded from disk rather than reusing the merged settings, so a one-off
            // --notes-root or --models-root never gets written into the settings file.
            AppSettings stored = services.SettingsStore.Load();
            if (string.Equals(stored.LastChannelId, channelId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            stored.LastChannelId = channelId;
            await services.SettingsStore.SaveAsync(stored, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            console.Warn($"could not remember the channel selection: {ex.Message}");
        }
    }

    /// <summary>Turns a missing endpoint into the documented exit code with an actionable message.</summary>
    public static CliException UnknownChannel(string requested, IReadOnlyList<ChannelEntry> entries)
    {
        var valid = entries.Count == 0
            ? "  (no active audio endpoints were found)"
            : string.Join(
                Environment.NewLine,
                entries.Select(e => $"  {e.Slug.PadRight(entries.Max(x => x.Slug.Length))}  {e.DisplayName}"));

        return new CliException(
            ExitCode.DeviceNotFound,
            $"No audio endpoint matches '{requested}'. Valid ids (see 'whispernotes devices'):"
            + Environment.NewLine + valid);
    }
}

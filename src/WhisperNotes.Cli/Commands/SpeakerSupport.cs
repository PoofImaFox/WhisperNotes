using System.CommandLine;
using System.Globalization;
using WhisperNotes.Cli.Rendering;
using WhisperNotes.Core.Composition;
using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.Cli.Commands;

/// <summary>Shared speaker-diarization lifecycle for live capture and imported media.</summary>
internal static class SpeakerSupport
{
    /// <summary>
    /// Resolves command flags onto settings. An explicit opt-out wins when both switches are
    /// present, because speaker labelling must never be forced over a user's direct instruction.
    /// </summary>
    public static void ApplyOptions(
        ParseResult parseResult,
        AppSettings settings,
        Option<bool> diarizeOption,
        Option<bool> noDiarizeOption,
        Option<int?> maxSpeakersOption)
    {
        if (parseResult.GetValue(noDiarizeOption))
        {
            settings.Diarization.Enabled = false;
        }
        else if (parseResult.GetValue(diarizeOption))
        {
            settings.Diarization.Enabled = true;
        }

        if (parseResult.GetValue(maxSpeakersOption) is { } cap)
        {
            if (cap < 1)
            {
                throw new CliException(ExitCode.Usage, "--max-speakers must be at least 1.");
            }

            settings.Diarization.MaxSpeakers = cap;
        }
    }

    /// <summary>
    /// Loads the anonymous voice model. Failure degrades to an unlabelled transcript; identifying
    /// speakers is enrichment and must never cost the words themselves.
    /// </summary>
    public static async Task<ISpeakerAttributor?> PrepareAsync(
        WhisperNotesServices services,
        ConsoleOutput console,
        CancellationToken cancellationToken)
    {
        DiarizationOptions options = services.Settings.ToDiarizationOptions();
        if (!options.Enabled)
        {
            return null;
        }

        try
        {
            ISpeakerAttributor attributor = await services.Diarizers
                .CreateAsync(options, cancellationToken)
                .ConfigureAwait(false);

            console.Diagnostic(
                "diarization up to "
                + options.MaxSpeakers.ToString(CultureInfo.InvariantCulture)
                + " speakers, merge threshold "
                + options.MergeThreshold.ToString(CultureInfo.InvariantCulture));

            return attributor;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.Warn($"speaker labelling is unavailable ({ex.Message}); the transcript will be written without it.");
            return null;
        }
    }

    /// <summary>
    /// Clusters the collected voice prints and stamps stable, session-local labels onto persisted
    /// entries. A returning voice receives its original number because clustering considers the
    /// complete recording before any label is applied.
    /// </summary>
    public static async Task<SpeakerTimeline?> AttributeAsync(
        WhisperNotesServices services,
        ConsoleOutput console,
        NoteSession session,
        IReadOnlyList<NoteEntry> entries,
        ISpeakerAttributor? attributor,
        int labelWidth)
    {
        if (attributor is not { IsAvailable: true } || attributor.Observed == 0 || entries.Count == 0)
        {
            return null;
        }

        using ProgressIndicator indicator = new(console, "diarizing", labelWidth);

        try
        {
            indicator.Report(null, Format.Count(attributor.Observed, "line", "lines"));

            SpeakerTimeline timeline = await Task.Run(attributor.Build).ConfigureAwait(false);
            indicator.Complete(1, Format.Count(timeline.SpeakerCount, "voice", "voices"));

            await SpeakerAttribution
                .IdentifyProfilesAsync(
                    timeline,
                    services.SpeakerProfiles,
                    services.Settings.ToDiarizationOptions().ProfileMatchThreshold,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!timeline.WorthLabelling && !timeline.HasNamedProfiles)
            {
                console.Field("speakers", "one voice throughout — left unlabelled", labelWidth);
                return timeline;
            }

            var labelled = await SpeakerAttribution
                .ApplyAsync(services.Notes, session.Id, entries, timeline, CancellationToken.None)
                .ConfigureAwait(false);

            console.Field(
                "speakers",
                $"{Format.Count(timeline.SpeakerCount, "voice", "voices")} across {Format.Count(labelled, "line", "lines")}",
                labelWidth);

            return timeline;
        }
        catch (Exception ex)
        {
            indicator.Abandon();
            console.Warn($"could not work out who spoke ({ex.Message}); the transcript itself is complete.");
            return null;
        }
    }
}

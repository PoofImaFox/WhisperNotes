using System.Text;
using System.Text.RegularExpressions;

namespace WhisperNotes.App.ViewModels;

/// <summary>
/// Finds and rewrites speaker labels in transcript Markdown without treating names in ordinary
/// prose as labels. The native exporter emits timestamped rows; a bare label is accepted only when
/// it is a diarization placeholder inside a <c>## Transcript</c> section.
/// </summary>
internal static partial class TranscriptSpeakerMarkup
{
    private static readonly char[] NewLineCharacters = ['\r', '\n'];

    public static IReadOnlyList<string> FindSpeakers(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var speakers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (SpeakerOccurrence occurrence in FindOccurrences(content))
        {
            if (seen.Add(occurrence.Speaker))
            {
                speakers.Add(occurrence.Speaker);
            }
        }

        return speakers;
    }

    public static string Rename(
        string content,
        string currentSpeaker,
        string newSpeaker,
        out int replacementCount)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSpeaker);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSpeaker);

        SpeakerOccurrence[] occurrences =
        [
            .. FindOccurrences(content)
                .Where(occurrence => string.Equals(
                    occurrence.Speaker,
                    currentSpeaker,
                    StringComparison.Ordinal))
        ];

        replacementCount = occurrences.Length;
        if (replacementCount == 0)
        {
            return content;
        }

        string encoded = EncodeInline(newSpeaker.Trim());
        var result = new StringBuilder(content);

        // Work backwards so the offsets gathered from the original text stay valid.
        for (int i = occurrences.Length - 1; i >= 0; i--)
        {
            SpeakerOccurrence occurrence = occurrences[i];
            result.Remove(occurrence.Start, occurrence.Length);
            result.Insert(occurrence.Start, encoded);
        }

        return result.ToString();
    }

    private static IEnumerable<SpeakerOccurrence> FindOccurrences(string content)
    {
        bool inTranscript = false;
        int lineStart = 0;

        while (lineStart < content.Length)
        {
            int lineEnd = content.IndexOfAny(NewLineCharacters, lineStart);
            if (lineEnd < 0)
            {
                lineEnd = content.Length;
            }

            string line = content[lineStart..lineEnd];
            if (TryReadHeading(line, out int headingLevel, out bool isTranscriptHeading))
            {
                if (headingLevel <= 2)
                {
                    inTranscript = isTranscriptHeading;
                }
            }
            else
            {
                Match transcriptMatch = SpeakerLine().Match(line);
                if (transcriptMatch.Success)
                {
                    Group encodedSpeaker = transcriptMatch.Groups["speaker"];
                    string speaker = DecodeInline(encodedSpeaker.Value).Trim();
                    bool timestamped = transcriptMatch.Groups["timestamp"].Success;

                    if (IsSpeakerLabel(speaker, timestamped, inTranscript))
                    {
                        yield return new SpeakerOccurrence(
                            speaker,
                            lineStart + encodedSpeaker.Index,
                            encodedSpeaker.Length);
                    }
                }
                else
                {
                    // The exporter also attributes action items using:
                    // "- [ ] do the thing — Speaker 1 (00:03:14)".
                    // Requiring that complete shape avoids touching prose that merely mentions a name.
                    Match actionItemMatch = ActionItemSpeaker().Match(line);
                    if (actionItemMatch.Success)
                    {
                        Group encodedSpeaker = actionItemMatch.Groups["speaker"];
                        string speaker = DecodeInline(encodedSpeaker.Value).Trim();
                        if (speaker.Length > 0)
                        {
                            yield return new SpeakerOccurrence(
                                speaker,
                                lineStart + encodedSpeaker.Index,
                                encodedSpeaker.Length);
                        }
                    }
                }
            }

            lineStart = lineEnd;
            if (lineStart < content.Length && content[lineStart] == '\r')
            {
                lineStart++;
            }

            if (lineStart < content.Length && content[lineStart] == '\n')
            {
                lineStart++;
            }
        }
    }

    private static bool IsSpeakerLabel(string label, bool timestamped, bool inTranscript)
    {
        if (label.Length == 0
            || string.Equals(label, "TODO", StringComparison.OrdinalIgnoreCase)
            || string.Equals(label, "MARKER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return timestamped
               || (inTranscript && DiarizationPlaceholder().IsMatch(label));
    }

    private static bool TryReadHeading(string line, out int level, out bool isTranscript)
    {
        ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
        level = 0;

        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level == 0 || level >= trimmed.Length || !char.IsWhiteSpace(trimmed[level]))
        {
            isTranscript = false;
            return false;
        }

        string title = trimmed[level..].Trim().TrimEnd('#').Trim().ToString();
        isTranscript = level == 2
                       && string.Equals(title, "Transcript", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string EncodeInline(string value)
    {
        var result = new StringBuilder(value.Length + 8);

        foreach (char character in value)
        {
            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~' or '#')
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static string DecodeInline(string value)
    {
        var result = new StringBuilder(value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\'
                && i + 1 < value.Length
                && value[i + 1] is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~' or '#')
            {
                i++;
            }

            result.Append(value[i]);
        }

        return result.ToString();
    }

    [GeneratedRegex(
        @"^\s*(?:(?<timestamp>\*\*\[\d{2,}:\d{2}:\d{2}\]\*\*)\s+)?\*\*(?<speaker>(?:\\.|[^*\r\n])+?):\*\*(?:\s|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpeakerLine();

    [GeneratedRegex(
        @"^\s*-\s+\[[ xX]\]\s+.+\s+—\s+(?<speaker>(?:\\.|[^\r\n])+?)\s+\(\d{2,}:\d{2}:\d{2}\)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ActionItemSpeaker();

    [GeneratedRegex(@"^Speaker\s+\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiarizationPlaceholder();

    private sealed record SpeakerOccurrence(string Speaker, int Start, int Length);
}

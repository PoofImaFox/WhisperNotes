using System.Text;
using WhisperNotes.Core.Audio;

namespace WhisperNotes.Cli.Audio;

/// <param name="Slug">Short, typeable id shown by <c>devices</c> and accepted by <c>--channel</c>.</param>
/// <param name="DisplayName">Friendly name with the enumerator's decorations stripped.</param>
internal sealed record ChannelEntry(string Slug, string DisplayName, AudioChannel Channel)
{
    public bool IsLoopback => Channel.Kind == AudioChannelKind.Loopback;

    /// <summary>What goes into <c>NoteSession.SourceDescription</c>.</summary>
    public string SourceDescription => (IsLoopback ? "Loopback: " : "Microphone: ") + DisplayName;
}

/// <summary>
/// Puts a short slug in front of every endpoint.
/// </summary>
/// <remarks>
/// Raw WASAPI endpoint ids look like <c>{0.0.0.00000000}.{9d2...}</c> — impossible to retype and
/// hostile in a shell. We derive a stable slug from the friendly name for humans, and still accept
/// the raw id so anything persisted in settings (or copied from the UI) keeps working.
/// </remarks>
internal static class ChannelCatalog
{
    private const int MaxSlugLength = 24;
    private const string DefaultSuffix = " (default)";
    private const string LoopbackMarker = "system audio";

    public static IReadOnlyList<ChannelEntry> Build(IAudioChannelEnumerator channels)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ChannelEntry>();

        foreach (AudioChannel channel in channels.GetChannels())
        {
            var display = CleanName(channel);
            entries.Add(new ChannelEntry(MakeUnique(Slugify(display), used), display, channel));
        }

        return entries;
    }

    public static ChannelEntry? Resolve(IReadOnlyList<ChannelEntry> entries, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        return Match(entries, value, static e => e.Slug)
               ?? Match(entries, value, static e => e.Channel.Id)
               ?? Match(entries, value, static e => e.DisplayName);
    }

    /// <summary>What <c>listen</c> falls back to: the default render endpoint, in loopback.</summary>
    public static ChannelEntry? PreferredDefault(IReadOnlyList<ChannelEntry> entries) =>
        entries.FirstOrDefault(static e => e.IsLoopback && e.Channel.IsDefault)
        ?? entries.FirstOrDefault(static e => e.IsLoopback)
        ?? entries.FirstOrDefault();

    /// <summary>
    /// Strips the decorations <c>WasapiChannelEnumerator</c> bakes into the name so the slug does
    /// not change when the user switches their default device.
    /// </summary>
    public static string CleanName(AudioChannel channel)
    {
        var name = channel.Name ?? string.Empty;

        if (name.EndsWith(DefaultSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^DefaultSuffix.Length];
        }

        var marker = name.LastIndexOf(LoopbackMarker, StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && marker + LoopbackMarker.Length == name.Length)
        {
            // The separator is an em dash today; trim every plausible dash rather than one literal.
            name = name[..marker].TrimEnd(' ', '-', '‒', '–', '—');
        }

        name = name.Trim();
        return name.Length == 0 ? channel.Id : name;
    }

    private static ChannelEntry? Match(
        IReadOnlyList<ChannelEntry> entries,
        string value,
        Func<ChannelEntry, string> selector)
    {
        foreach (ChannelEntry entry in entries)
        {
            if (string.Equals(selector(entry), value, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string Slugify(string name)
    {
        var words = new List<string>();
        var word = new StringBuilder();

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        // Truncate on word boundaries: "speakers-realtek-high" reads far better than
        // "speakers-realtek-high-de".
        var slug = new StringBuilder();
        foreach (var part in words)
        {
            if (slug.Length > 0 && slug.Length + 1 + part.Length > MaxSlugLength)
            {
                break;
            }

            if (slug.Length > 0)
            {
                slug.Append('-');
            }

            slug.Append(part);
        }

        var result = slug.ToString();
        if (result.Length == 0)
        {
            result = words.Count > 0 ? words[0][..Math.Min(words[0].Length, MaxSlugLength)] : "endpoint";
        }

        return result;
    }

    private static string MakeUnique(string slug, HashSet<string> used)
    {
        if (used.Add(slug))
        {
            return slug;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = slug + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}

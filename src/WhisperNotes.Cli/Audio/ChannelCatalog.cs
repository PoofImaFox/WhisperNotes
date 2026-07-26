using System.Text;
using WhisperNotes.Core.Audio;

namespace WhisperNotes.Cli.Audio;

/// <param name="Slug">Short, typeable id shown by <c>devices</c> and accepted by <c>--channel</c>.</param>
/// <param name="DisplayName">Friendly name with the enumerator's decorations stripped.</param>
internal sealed record ChannelEntry(string Slug, string DisplayName, AudioChannel Channel)
{
    public bool IsLoopback => Channel.Kind == AudioChannelKind.Loopback;

    /// <summary>True when this taps one application's render stream rather than a whole endpoint.</summary>
    public bool IsApplication => Channel.Kind == AudioChannelKind.Application;

    /// <summary>What goes into <c>NoteSession.SourceDescription</c>.</summary>
    /// <remarks>
    /// Application sources say so out loud when the machine cannot actually isolate them: the capture
    /// factory falls back to device loopback below build
    /// <see cref="ProcessLoopbackSupport.MinimumBuild"/>, and a note recorded as "Application: Teams"
    /// when it in fact contains everything the machine played would be a lie in the archive.
    /// </remarks>
    public string SourceDescription => Channel.Kind switch
    {
        AudioChannelKind.Loopback => "Loopback: " + DisplayName,
        AudioChannelKind.Application => ProcessLoopbackSupport.IsSupported
            ? "Application: " + DisplayName
            : "Application: " + DisplayName + " (system audio fallback)",
        _ => "Microphone: " + DisplayName,
    };
}

/// <summary>
/// Puts a short slug in front of every endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Raw WASAPI endpoint ids look like <c>{0.0.0.00000000}.{9d2...}</c> — impossible to retype and
/// hostile in a shell. We derive a stable slug from the friendly name for humans, and still accept
/// the raw id so anything persisted in settings (or copied from the UI) keeps working.
/// </para>
/// <para>
/// Application channels carry a readable id already (<c>app:teams.exe</c>), so for those the slug is
/// a convenience rather than a rescue — see <see cref="SlugSource"/> for why it is derived from the
/// executable and not the name.
/// </para>
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
            var slug = MakeUnique(Slugify(SlugSource(channel, display)), used);
            entries.Add(new ChannelEntry(slug, display, channel));
        }

        return entries;
    }

    /// <summary>Finds the entry a user meant by <paramref name="value"/>, or null.</summary>
    /// <remarks>
    /// Four keys, most specific first: the slug we printed, the raw endpoint id (which covers
    /// <c>app:teams.exe</c> without any special case, because that is literally
    /// <see cref="AudioChannel.Id"/>), the bare executable name, then the display name.
    /// <para>
    /// The executable pass is the one deliberate convenience: <c>--channel teams.exe</c> is what
    /// people type after reading the id off <c>devices</c>, and there is nothing else it could mean.
    /// It sits after the id pass so a literal id always wins, and it can never hijack a device — an
    /// endpoint's <see cref="AudioChannel.ExecutableName"/> is null.
    /// </para>
    /// </remarks>
    public static ChannelEntry? Resolve(IReadOnlyList<ChannelEntry> entries, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        return Match(entries, value, static e => e.Slug)
               ?? Match(entries, value, static e => e.Channel.Id)
               ?? Match(entries, value, static e => e.Channel.ExecutableName ?? string.Empty)
               ?? Match(entries, value, static e => e.DisplayName);
    }

    /// <summary>What <c>listen</c> falls back to: the default render endpoint, in loopback.</summary>
    /// <remarks>
    /// Applications are excluded from every tier on purpose. Picking one implicitly would silently
    /// scope a recording to whatever happened to be playing — and on a machine below
    /// <see cref="ProcessLoopbackSupport.MinimumBuild"/> it would not even do that. An application is
    /// only ever captured because the user named it.
    /// </remarks>
    public static ChannelEntry? PreferredDefault(IReadOnlyList<ChannelEntry> entries) =>
        entries.FirstOrDefault(static e => e.IsLoopback && e.Channel.IsDefault)
        ?? entries.FirstOrDefault(static e => e.IsLoopback)
        ?? entries.FirstOrDefault(static e => !e.IsApplication);

    /// <summary>
    /// Strips the decorations <c>WasapiChannelEnumerator</c> bakes into the name so the slug does
    /// not change when the user switches their default device.
    /// </summary>
    /// <remarks>
    /// The two decorations only ever appear on endpoints, but the stripping is applied to every kind
    /// so that an application whose window title happens to end in one of them is not a special case.
    /// The empty-name fallback is what differs: showing a user <c>app:teams.exe</c> when we know the
    /// image name is <c>teams.exe</c> is needlessly cryptic.
    /// </remarks>
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
        if (name.Length > 0)
        {
            return name;
        }

        return Executable(channel) ?? channel.Id;
    }

    /// <summary>
    /// The text <see cref="Slugify"/> works from — the display name for endpoints, the executable
    /// for applications.
    /// </summary>
    /// <remarks>
    /// An application's name is whatever the session reports, which is routinely a window title:
    /// "Inbox — Microsoft Teams" slugs to <c>inbox-microsoft-teams</c> and becomes
    /// <c>calendar-microsoft-teams</c> the moment the user clicks a different tab. Slugs are meant to
    /// be typed once and reused, so applications slug from the image name instead — stable across
    /// restarts, already short, and the same string the persisted id is keyed on.
    /// </remarks>
    private static string SlugSource(AudioChannel channel, string display)
    {
        if (channel.Kind != AudioChannelKind.Application)
        {
            return display;
        }

        var executable = Executable(channel);
        if (executable is null)
        {
            return display;
        }

        // "ms-teams.exe" -> "ms-teams": kept, the extension would put a trailing "-exe" on the slug
        // of every application alike, which distinguishes nothing and just eats the length budget.
        var stem = Path.GetFileNameWithoutExtension(executable);
        return stem.Length == 0 ? display : stem;
    }

    /// <summary>
    /// The image name behind an application channel — from the record, or recovered from the id when
    /// the entry came back out of settings rather than from a live enumeration.
    /// </summary>
    private static string? Executable(AudioChannel channel)
    {
        var executable = channel.ExecutableName;
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = ApplicationChannelId.ExecutableOf(channel.Id);
        }

        return string.IsNullOrWhiteSpace(executable) ? null : executable.Trim();
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

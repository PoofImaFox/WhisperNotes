using WhisperNotes.Core.Audio;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.App.DesignData;

/// <summary>Plausible content shared by every design-time fake so the app looks real without Core.</summary>
internal static class SampleData
{
    public static IReadOnlyList<AudioChannel> Channels { get; } =
    [
        new("{0.0.0.00000000}.{a1b2c3d4-render-default}", "Speakers (Realtek(R) Audio)", AudioChannelKind.Loopback, true, 48_000, 2),
        new("{0.0.0.00000000}.{ff11ee22-render-vbcable}", "CABLE Input (VB-Audio Virtual Cable)", AudioChannelKind.Loopback, false, 48_000, 2),
        new("{0.0.0.00000000}.{9c8b7a65-render-dell}", "DELL U2723QE (NVIDIA High Definition Audio)", AudioChannelKind.Loopback, false, 48_000, 2),
        new("{0.0.0.00000000}.{31415926-render-jabra}", "Headset Earphone (Jabra Evolve2 65)", AudioChannelKind.Loopback, false, 44_100, 2),
        new("{0.0.1.00000000}.{27182818-capture-jabra}", "Headset Microphone (Jabra Evolve2 65)", AudioChannelKind.Microphone, true, 16_000, 1),
        new("{0.0.1.00000000}.{16180339-capture-array}", "Microphone Array (Intel Smart Sound)", AudioChannelKind.Microphone, false, 48_000, 2),
        new("{0.0.1.00000000}.{57721566-capture-vbcable}", "CABLE Output (VB-Audio Virtual Cable)", AudioChannelKind.Microphone, false, 48_000, 2),
    ];

    /// <summary>Lines the fake engine emits, in order, so the live view reads like a real meeting.</summary>
    public static IReadOnlyList<string> DictationScript { get; } =
    [
        "Right, let's pick up where we left off on the migration cutover.",
        "The blocker is still the SQL Always On listener — DNS hasn't propagated to the branch sites.",
        "I'd rather we don't cut over on the Friday, it leaves nobody on site if it goes wrong.",
        "Agreed. Let's move the window to Tuesday the eleventh, eight PM to midnight.",
        "Can you confirm the backup window doesn't overlap? Veeam kicks off at ten.",
        "I'll shift the Veeam job to two AM for that night only and put it back afterwards.",
        "On licensing — we're four E3 seats short once the new starters land in August.",
        "Procurement said they can add seats mid-term, it just co-terms to the March renewal.",
        "One more thing, the firewall rules for the reporting VLAN still haven't been signed off.",
        "That's with Dave. I'll chase him and copy you so it's on record.",
        "Let's also agree the rollback criteria before the change board on Thursday.",
        "If replication lag goes over five minutes we abort and fail back to the old cluster.",
        "Fine. I'll write that into the change request as the abort condition.",
        "Last item — invoicing. This session and the two site visits go on the July timesheet.",
        "Understood. I'll get the timesheet over to you by Friday close of play.",
    ];

    private static IReadOnlyList<string> ActionItems { get; } =
    [
        "Chase Dave for firewall rule sign-off before Thursday's change board.",
        "Move the Veeam job to 02:00 for the cutover night, revert the morning after.",
        "Raise the change request with the five-minute replication-lag abort condition.",
        "Order four additional E3 seats, co-termed to the March renewal.",
        "Send July timesheet including this session and both site visits.",
    ];

    private static IReadOnlyList<string> Markers { get; } =
    [
        "DECISION: cutover moved to Tuesday 11th, 20:00–00:00.",
        "DECISION: rollback if replication lag exceeds five minutes.",
        "Client confirmed budget approval verbally on this call.",
    ];

    private static IReadOnlyList<string> ManualNotes { get; } =
    [
        "(Anna joined late, missed the first five minutes.)",
        "(Screen shared: current AD site topology.)",
        "(Audio dropped for ~20s here, recap given afterwards.)",
    ];

    /// <summary>
    /// What diarization produces before anyone renames anything — the clusters are real, the
    /// names are not knowable from audio. Here so the previewer shows the chip and the picker.
    /// </summary>
    private static IReadOnlyList<string> SpeakerLabels { get; } = ["Speaker 1", "Speaker 2", "Speaker 3"];

    // Declaration order matters: these two run their initialisers after every list above.
    public static IReadOnlyList<NoteSession> Sessions { get; } = BuildSessions();

    public static IReadOnlyDictionary<string, IReadOnlyList<NoteEntry>> Entries { get; } = BuildEntries();

    private static IReadOnlyList<NoteSession> BuildSessions()
    {
        // Anchored to "now" so the browser always shows today/yesterday/last week rather than stale dates.
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        return
        [
            // Relative to "now" so today's rows are always in the past, whatever time the app is opened.
            Session("s-2026-0725-1430", "Migration cutover planning", "Northwind Logistics",
                now.AddHours(-2), TimeSpan.FromMinutes(52),
                "Loopback: Speakers (Realtek(R) Audio)", ["migration", "sql"], "Base"),
            Session("s-2026-0725-0915", "Daily standup", "Northwind Logistics",
                now.AddHours(-5), TimeSpan.FromMinutes(14),
                "Loopback: Speakers (Realtek(R) Audio)", ["standup"], "Base"),
            Session("s-2026-0724-1100", "Firewall change review", "Northwind Logistics",
                today.AddDays(-1).AddHours(11), TimeSpan.FromMinutes(38),
                "Loopback: CABLE Input (VB-Audio Virtual Cable)", ["security", "change"], "Small"),
            Session("s-2026-0724-1600", "Quarterly service review", "Halcyon Care Group",
                today.AddDays(-1).AddHours(16), TimeSpan.FromMinutes(64),
                "Loopback: Speakers (Realtek(R) Audio)", ["qbr", "billable"], "Small"),
            Session("s-2026-0722-1000", "Tenant migration kickoff", "Halcyon Care Group",
                today.AddDays(-3).AddHours(10), TimeSpan.FromMinutes(75),
                "Loopback: Speakers (Realtek(R) Audio)", ["m365", "kickoff"], "Medium"),
            Session("s-2026-0718-1345", "Backup failure post-mortem", "Ashgrove Chambers",
                today.AddDays(-7).AddHours(13).AddMinutes(45), TimeSpan.FromMinutes(41),
                "video: teams-recording-postmortem.mp4", ["veeam", "incident"], "Medium"),
            Session("s-2026-0716-0930", "Scoping call — new starter onboarding", null,
                today.AddDays(-9).AddHours(9).AddMinutes(30), TimeSpan.FromMinutes(23),
                "Loopback: Headset Earphone (Jabra Evolve2 65)", ["scoping"], "Base"),
        ];
    }

    private static NoteSession Session(
        string id, string title, string? project, DateTimeOffset started, TimeSpan duration,
        string source, string[] tags, string model) =>
        new(id, title, project, started, started + duration, source, tags, model);

    private static IReadOnlyDictionary<string, IReadOnlyList<NoteEntry>> BuildEntries()
    {
        var map = new Dictionary<string, IReadOnlyList<NoteEntry>>(StringComparer.Ordinal);

        foreach (var session in Sessions)
        {
            var entries = new List<NoteEntry>();
            var offset = TimeSpan.FromSeconds(4);
            var seed = session.Id.Aggregate(17, (acc, c) => unchecked(acc * 31 + c)) & 0x7FFF;
            var rng = new Random(seed);
            var count = 8 + rng.Next(10);

            for (var i = 0; i < count; i++)
            {
                var kind = (i, rng.Next(10)) switch
                {
                    (_, 0) => NoteEntryKind.ActionItem,
                    (_, 1) => NoteEntryKind.Marker,
                    (_, 2) => NoteEntryKind.Manual,
                    _ => NoteEntryKind.Dictation
                };

                var text = kind switch
                {
                    NoteEntryKind.ActionItem => ActionItems[rng.Next(ActionItems.Count)],
                    NoteEntryKind.Marker => Markers[rng.Next(Markers.Count)],
                    NoteEntryKind.Manual => ManualNotes[rng.Next(ManualNotes.Count)],
                    _ => DictationScript[(i + seed) % DictationScript.Count]
                };

                entries.Add(new NoteEntry(
                    $"{session.Id}-e{i:D3}",
                    session.StartedUtc + offset,
                    offset,
                    kind,
                    text,
                    // Only dictation carries a voice: a typed note, marker or action item was
                    // never spoken, so there is nothing for diarization to attribute.
                    Speaker: kind == NoteEntryKind.Dictation ? SpeakerLabels[i % SpeakerLabels.Count] : null,
                    Confidence: kind == NoteEntryKind.Dictation ? 0.62f + (float)rng.NextDouble() * 0.36f : null));

                offset += TimeSpan.FromSeconds(12 + rng.Next(140));
            }

            map[session.Id] = entries;
        }

        return map;
    }
}

namespace WhisperNotes.Core.Diarization;

/// <summary>A stretch of the recording attributed to one speaker.</summary>
/// <param name="Start">Offset from the start of the recording.</param>
/// <param name="End">Exclusive end of the turn.</param>
/// <param name="Speaker">Zero-based speaker index, stable across the whole timeline.</param>
public readonly record struct SpeakerTurn(TimeSpan Start, TimeSpan End, int Speaker)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>
/// Who held the floor, and when, for one recording. Clustering produces numbered local speakers
/// and representative voiceprints; profile matching may then attach a durable user-supplied name.
/// Until that happens, the honest display form remains "Speaker 2".
/// </summary>
public sealed class SpeakerTimeline
{
    /// <summary>
    /// How far outside every turn a line may fall and still be attributed to the nearest one.
    /// Whisper's timestamps and our own segmentation drift against each other by a little, so a
    /// line landing just past the edge of a turn is a rounding artefact rather than a new speaker.
    /// </summary>
    private static readonly TimeSpan NearestTurnTolerance = TimeSpan.FromSeconds(2);

    private readonly SpeakerTurn[] _turns;
    private readonly float[][] _voicePrints;
    private readonly SpeakerVoiceProfile?[] _profiles;

    public SpeakerTimeline(
        IReadOnlyList<SpeakerTurn> turns,
        int speakerCount,
        IReadOnlyList<float[]>? voicePrints = null)
    {
        _turns = turns is null ? [] : [.. turns.Where(t => t.End > t.Start).OrderBy(t => t.Start)];
        SpeakerCount = Math.Max(speakerCount, 0);
        _voicePrints = new float[SpeakerCount][];
        _profiles = new SpeakerVoiceProfile?[SpeakerCount];

        for (var i = 0; i < _voicePrints.Length; i++)
        {
            _voicePrints[i] = voicePrints is not null && i < voicePrints.Count
                ? [.. voicePrints[i]]
                : [];
        }
    }

    /// <summary>The result when diarization was switched off, unavailable, or found no speech.</summary>
    public static SpeakerTimeline Unattributed { get; } = new([], 0);

    public IReadOnlyList<SpeakerTurn> Turns => _turns;

    /// <summary>How many distinct voices were found.</summary>
    public int SpeakerCount { get; }

    /// <summary>True when nothing was attributed and callers should leave the speaker unset.</summary>
    public bool IsEmpty => _turns.Length == 0;

    /// <summary>
    /// Whether stamping these labels onto a transcript tells the reader anything. One voice for a
    /// whole recording is the normal case for dictation, and prefixing every line of it with
    /// "Speaker 1:" adds clutter in exchange for no information.
    /// </summary>
    public bool WorthLabelling => SpeakerCount > 1 && _turns.Length > 0;

    /// <summary>
    /// True when matching resolved at least one local cluster to a name the user supplied earlier.
    /// A known single-speaker recording is useful to label even though an anonymous one is not.
    /// </summary>
    public bool HasNamedProfiles => _profiles.Any(profile => !string.IsNullOrWhiteSpace(profile?.Name));

    /// <summary>
    /// Retrieves the representative normalized voiceprint for one session-local speaker. Older
    /// timelines and test doubles may not carry one, in which case this returns false.
    /// </summary>
    public bool TryGetVoicePrint(int speaker, out ReadOnlyMemory<float> voicePrint)
    {
        if (speaker >= 0 &&
            speaker < _voicePrints.Length &&
            _voicePrints[speaker].Length > 0)
        {
            voicePrint = _voicePrints[speaker];
            return true;
        }

        voicePrint = ReadOnlyMemory<float>.Empty;
        return false;
    }

    /// <summary>The durable profile matched to a local speaker, after profile identification.</summary>
    public SpeakerVoiceProfile? ProfileFor(int speaker) =>
        speaker >= 0 && speaker < _profiles.Length ? _profiles[speaker] : null;

    /// <summary>The durable profile responsible for the supplied transcript span, if identified.</summary>
    public SpeakerVoiceProfile? Profile(TimeSpan start, TimeSpan end) =>
        Resolve(start, end) is { } speaker ? ProfileFor(speaker) : null;

    /// <summary>
    /// The speaker who did most of the talking across <paramref name="start"/>..<paramref name="end"/>,
    /// or null if the range cannot be attributed. Majority overlap rather than whoever starts the
    /// range: a transcript line that begins on the tail of one voice and continues into another
    /// belongs to whoever actually said most of it.
    /// </summary>
    public int? Resolve(TimeSpan start, TimeSpan end)
    {
        if (_turns.Length == 0)
        {
            return null;
        }

        if (end <= start)
        {
            end = start;
        }

        Span<double> overlap = SpeakerCount <= 32 ? stackalloc double[SpeakerCount] : new double[SpeakerCount];
        var best = -1;
        var bestOverlap = 0d;

        for (var i = FirstTurnEndingAfter(start); i < _turns.Length && _turns[i].Start < end; i++)
        {
            SpeakerTurn turn = _turns[i];
            if (turn.Speaker < 0 || turn.Speaker >= overlap.Length)
            {
                continue;
            }

            var shared = (Min(turn.End, end) - Max(turn.Start, start)).TotalSeconds;
            if (shared <= 0)
            {
                continue;
            }

            overlap[turn.Speaker] += shared;
            if (overlap[turn.Speaker] > bestOverlap)
            {
                bestOverlap = overlap[turn.Speaker];
                best = turn.Speaker;
            }
        }

        return best >= 0 ? best : Nearest(start, end);
    }

    /// <summary>Display form of a speaker index; one-based because "Speaker 0" reads as a bug.</summary>
    public static string LabelFor(int speaker) => $"Speaker {speaker + 1}";

    /// <summary>Convenience for the transcription pipeline: the label to stamp on a line, or null.</summary>
    public string? Label(TimeSpan start, TimeSpan end) =>
        Resolve(start, end) is { } speaker
            ? ProfileFor(speaker)?.Name ?? LabelFor(speaker)
            : null;

    internal void SetProfile(int speaker, SpeakerVoiceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (speaker < 0 || speaker >= _profiles.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(speaker));
        }

        _profiles[speaker] = profile;
    }

    /// <summary>
    /// A zero-length range, or one that falls entirely in a silence between turns, still wants an
    /// answer. Only the immediate neighbourhood counts — attributing a line to a voice heard a
    /// minute earlier would be a guess dressed up as a fact.
    /// </summary>
    private int? Nearest(TimeSpan start, TimeSpan end)
    {
        var best = -1;
        TimeSpan bestDistance = NearestTurnTolerance;

        for (var i = 0; i < _turns.Length; i++)
        {
            SpeakerTurn turn = _turns[i];
            if (turn.Speaker < 0 || turn.Speaker >= SpeakerCount)
            {
                continue;
            }

            TimeSpan distance = turn.Start >= end ? turn.Start - end
                : turn.End <= start ? start - turn.End
                : TimeSpan.Zero;

            // Strictly closer, so a tie goes to the turn that came first: when a line sits exactly
            // between two speakers, the one who had just been talking is the better guess.
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = turn.Speaker;
            }

            if (turn.Start >= end && distance > bestDistance)
            {
                break;
            }
        }

        return best >= 0 ? best : null;
    }

    /// <summary>Binary search for where an overlap scan has to begin. Turns are sorted and disjoint.</summary>
    private int FirstTurnEndingAfter(TimeSpan start)
    {
        var low = 0;
        var high = _turns.Length;

        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_turns[mid].End <= start)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>How hard to look for separate voices, and where to stop.</summary>
public sealed record DiarizationOptions
{
    public static DiarizationOptions Default { get; } = new();

    /// <summary>
    /// Off by default is the wrong trade here — a transcript that does not say who spoke is the
    /// thing this exists to fix — but it costs a model download and a little CPU, so it stays a
    /// switch rather than an assumption.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Ceiling on how many voices may be reported. Meetings this app is pointed at are single-digit
    /// affairs; a threshold that decides there were nineteen people is wrong in a way that is worse
    /// than merging two of them, so the count is capped rather than trusted.
    /// </summary>
    public int MaxSpeakers { get; init; } = 8;

    /// <summary>
    /// Cosine distance at which two groups of utterances stop counting as the same voice. Raise it
    /// to merge more, lower it to split more.
    /// </summary>
    /// <remarks>
    /// Set below the middle of the range that works, because the two ways of being wrong are not
    /// equally bad. Splitting one person into two speakers is visible and repairable — renaming
    /// both to the same person merges them. Fusing two people under one label is neither: the
    /// transcript reads as though one person said all of it, and no rename can pull them apart.
    /// </remarks>
    public double MergeThreshold { get; init; } = 0.6;

    /// <summary>
    /// Maximum cosine distance between a newly detected cluster and a durable voice profile before
    /// the cluster is treated as a new acoustic identity.
    /// </summary>
    public double ProfileMatchThreshold { get; init; } = 0.35;

    /// <summary>
    /// Below this a line is not used as evidence of who anyone is; it gets its speaker from the
    /// turns around it instead. Set to twice the shortest usable sample rather than to one,
    /// because every line is checked by embedding its two halves separately — so half of the
    /// shortest line accepted still has to be enough audio to identify someone by.
    /// </summary>
    public TimeSpan MinObservation { get; init; } = TimeSpan.FromSeconds(1.2);

    /// <summary>
    /// Only this much of any one line is embedded. Past a few seconds the extra audio stops
    /// sharpening the vector, and a long line is also the most likely place for a second voice to
    /// have crept in — which would blur the very thing being measured.
    /// </summary>
    public TimeSpan MaxObservation { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>Turns a window of audio into a vector that is close to other windows of the same voice.</summary>
public interface ISpeakerEmbedder : IDisposable
{
    /// <summary>Length of the vectors this model produces.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Embeds one window of 16 kHz mono PCM in -1..1. The result is L2-normalised, so two windows
    /// are compared with a plain dot product.
    /// </summary>
    float[] Embed(ReadOnlySpan<float> pcm16kMono);
}

/// <summary>
/// Listens to each decoded segment as it goes past and, once the whole recording has been heard,
/// works out how many voices there were.
/// </summary>
/// <remarks>
/// <para>
/// Two decisions are baked into this shape. First, it rides on the transcriber's own segment
/// boundaries rather than running a second voice-activity pass: whisper has already cut the audio
/// where people stop talking, and reusing those cuts means the speaker timeline and the transcript
/// lines are aligned by construction instead of by two independent estimates that drift apart.
/// </para>
/// <para>
/// Second, observation is streaming but the answer is not. Deciding how many people are in a room
/// is a judgement about the whole recording — the second speaker is only recognisable as a second
/// speaker once there is a first to compare against — so nothing is labelled until
/// <see cref="Build"/> has seen everything.
/// </para>
/// </remarks>
public interface ISpeakerAttributor : IDisposable
{
    /// <summary>True when the model loaded and observations will actually be used.</summary>
    bool IsAvailable { get; }

    /// <summary>How many segments have been observed. Zero means <see cref="Build"/> has nothing to do.</summary>
    int Observed { get; }

    /// <summary>
    /// Records the voice heard between <paramref name="start"/> and <paramref name="end"/>.
    /// <paramref name="audio"/> is the 16 kHz mono buffer the segment was decoded from, which
    /// begins at <paramref name="audioOffset"/> in the recording; the implementation slices the
    /// segment out of it, so the caller does not have to.
    /// </summary>
    void Observe(TimeSpan start, TimeSpan end, ReadOnlySpan<float> audio, TimeSpan audioOffset);

    /// <summary>
    /// Clusters everything observed. Returns <see cref="SpeakerTimeline.Unattributed"/> rather than
    /// throwing when there is too little to go on — a recording of one person, or of nobody, is not
    /// an error, and neither is a model that failed to load.
    /// </summary>
    SpeakerTimeline Build();
}

/// <summary>Builds an attributor, fetching the embedding model on first use.</summary>
public interface ISpeakerAttributorFactory
{
    /// <summary>
    /// Downloads the model if it is missing and loads it. Throws if the model cannot be obtained;
    /// callers are expected to treat that as "transcribe without speaker labels" rather than as a
    /// failed transcription, because the words are worth more than the attribution.
    /// </summary>
    Task<ISpeakerAttributor> CreateAsync(DiarizationOptions options, CancellationToken cancellationToken);
}

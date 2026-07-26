using WhisperNotes.Core.Audio;

namespace WhisperNotes.Core.Diarization;

/// <summary>
/// Collects one voice print per decoded line and, at the end, works out how many people were in the
/// room.
/// </summary>
/// <remarks>
/// One embedding per transcript line, rather than several per line, is a deliberate limit. A line is
/// the smallest thing that can carry a speaker label in the notes — its text cannot be split without
/// word-level timings we do not have — so sampling a long line four times could only ever produce
/// four votes for the one label it is going to get anyway. The extra vectors would cost quadratic
/// clustering time to buy a tie-break.
/// </remarks>
internal sealed class SpeakerAttributor : ISpeakerAttributor
{
    /// <summary>
    /// Same-speaker lines separated by less than this are folded into one turn. Purely cosmetic —
    /// attribution reads the same either way — but it keeps the timeline from listing one turn per
    /// breath for anyone talking at length.
    /// </summary>
    private static readonly TimeSpan TurnJoin = TimeSpan.FromSeconds(2);

    /// <summary>
    /// RMS below this is room tone rather than anybody talking. Same value the live chunker uses to
    /// decide the same question.
    /// </summary>
    private const float VoiceFloor = 0.006f;

    /// <summary>20 ms — fine enough to place the edge of a pause, long enough for a stable RMS.</summary>
    private const int VoiceWindowSamples = AudioFrame.SampleRate / 50;

    private readonly ISpeakerEmbedder? _embedder;
    private readonly DiarizationOptions _options;
    private readonly List<float[]> _embeddings = [];
    private readonly List<double> _weights = [];
    private readonly List<(TimeSpan Start, TimeSpan End)> _spans = [];

    private bool _disposed;

    /// <param name="embedder">
    /// Null when the model could not be loaded. That is not fatal: the transcript is still worth
    /// having without speaker labels, so the attributor degrades to doing nothing.
    /// </param>
    public SpeakerAttributor(ISpeakerEmbedder? embedder, DiarizationOptions? options = null)
    {
        _embedder = embedder;
        _options = options ?? DiarizationOptions.Default;
    }

    public bool IsAvailable => _embedder is not null && _options.Enabled;

    public int Observed => _embeddings.Count;

    public void Observe(TimeSpan start, TimeSpan end, ReadOnlySpan<float> audio, TimeSpan audioOffset)
    {
        if (_disposed || _embedder is null || !_options.Enabled || end <= start)
        {
            return;
        }

        var from = Math.Clamp(ToSamples(start - audioOffset), 0, audio.Length);
        var to = Math.Clamp(ToSamples(end - audioOffset), from, audio.Length);

        var available = to - from;
        var minimum = ToSamples(_options.MinObservation);
        if (available < minimum)
        {
            return;
        }

        var window = Math.Min(available, ToSamples(_options.MaxObservation));
        ReadOnlySpan<float> slice = audio.Slice(from, window);

        // The threshold is against *speech*, not against elapsed time. A line that is one word and
        // three seconds of room tone is mostly silence, and silence embeds very consistently — so
        // it passes the coherence check below, then clusters with every other near-silent line into
        // a speaker who is really just the sound of the room.
        if (Voiced(slice) < minimum)
        {
            return;
        }

        // Every observation is checked against itself; no line is taken on trust. That is why
        // MinObservation is twice the length of a usable sample rather than one — half of the
        // shortest line accepted here still has to be enough audio to identify someone by.
        float[]? embedding = Coherent(slice);

        if (embedding is not { Length: > 0 })
        {
            return;
        }

        _embeddings.Add(embedding);
        _weights.Add((double)window / AudioFrame.SampleRate);
        _spans.Add((start, end));
    }

    public SpeakerTimeline Build()
    {
        if (_embeddings.Count == 0)
        {
            return SpeakerTimeline.Unattributed;
        }

        int[] speakers = SpeakerClustering.Cluster(
            _embeddings, _weights, _options.MergeThreshold, _options.MaxSpeakers);

        if (speakers.Length != _spans.Count)
        {
            return SpeakerTimeline.Unattributed;
        }

        var count = 0;
        List<SpeakerTurn> turns = new(speakers.Length);

        for (var i = 0; i < speakers.Length; i++)
        {
            var speaker = speakers[i];
            count = Math.Max(count, speaker + 1);

            (TimeSpan start, TimeSpan end) = _spans[i];

            if (turns.Count > 0)
            {
                SpeakerTurn previous = turns[^1];
                if (previous.Speaker == speaker && start - previous.End <= TurnJoin)
                {
                    turns[^1] = previous with { End = end > previous.End ? end : previous.End };
                    continue;
                }
            }

            turns.Add(new SpeakerTurn(start, end, speaker));
        }

        return new SpeakerTimeline(turns, count, BuildVoicePrints(speakers, count));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _embedder?.Dispose();

        _embeddings.Clear();
        _weights.Clear();
        _spans.Clear();
    }

    /// <summary>
    /// Collapses all observations assigned to each cluster into one normalized, duration-weighted
    /// voiceprint suitable for matching across sessions.
    /// </summary>
    private float[][] BuildVoicePrints(IReadOnlyList<int> speakers, int count)
    {
        if (count == 0 || _embeddings.Count == 0)
        {
            return [];
        }

        int dimensions = _embeddings[0].Length;
        double[][] sums = Enumerable.Range(0, count)
            .Select(_ => new double[dimensions])
            .ToArray();

        for (var observation = 0; observation < speakers.Count; observation++)
        {
            int speaker = speakers[observation];
            float[] embedding = _embeddings[observation];
            if (speaker < 0 || speaker >= sums.Length || embedding.Length != dimensions)
            {
                continue;
            }

            double weight = _weights[observation];
            for (var dimension = 0; dimension < dimensions; dimension++)
            {
                sums[speaker][dimension] += embedding[dimension] * weight;
            }
        }

        float[][] voicePrints = new float[count][];
        for (var speaker = 0; speaker < count; speaker++)
        {
            double magnitude = Math.Sqrt(sums[speaker].Sum(value => value * value));
            if (magnitude <= 0 || !double.IsFinite(magnitude))
            {
                voicePrints[speaker] = [];
                continue;
            }

            voicePrints[speaker] = sums[speaker]
                .Select(value => (float)(value / magnitude))
                .ToArray();
        }

        return voicePrints;
    }

    /// <summary>
    /// Embeds both halves of a window and returns their mean, or null if the two halves do not
    /// sound like the same person.
    /// </summary>
    /// <remarks>
    /// A transcript line is not guaranteed to hold one voice. The transcriber cuts on pauses, and
    /// people interrupt each other in the pauses, so a line that begins on the tail of one speaker
    /// and finishes in the mouth of the next is common. Embedding that whole line produces a vector
    /// that belongs to neither of them — and because every such line lands in the same place
    /// between the real speakers, they cluster with each other and invent a person who was never
    /// in the room.
    /// <para>
    /// Dropping those lines is better than guessing at them: an unobserved line is still attributed
    /// afterwards, from the turns either side of it, which is at worst half right rather than
    /// confidently wrong. The bar for agreement is the clustering threshold itself — if two halves
    /// would not be grouped as one voice, they are not evidence of one voice.
    /// </para>
    /// </remarks>
    private float[]? Coherent(ReadOnlySpan<float> window)
    {
        var half = window.Length / 2;
        float[] opening = Embed(window[..half]);
        float[] closing = Embed(window[half..]);

        if (opening.Length == 0 || opening.Length != closing.Length)
        {
            return null;
        }

        double similarity = 0;
        for (var i = 0; i < opening.Length; i++)
        {
            similarity += (double)opening[i] * closing[i];
        }

        if (1 - similarity > _options.MergeThreshold)
        {
            return null;
        }

        float[] mean = new float[opening.Length];
        double sum = 0;

        for (var i = 0; i < mean.Length; i++)
        {
            mean[i] = (opening[i] + closing[i]) * 0.5f;
            sum += (double)mean[i] * mean[i];
        }

        var norm = Math.Sqrt(sum);
        if (!double.IsFinite(norm) || norm <= 0)
        {
            return null;
        }

        var scale = (float)(1 / norm);
        for (var i = 0; i < mean.Length; i++)
        {
            mean[i] *= scale;
        }

        return mean;
    }

    /// <summary>How many of the window's samples fall in a stretch loud enough to be speech.</summary>
    private static int Voiced(ReadOnlySpan<float> window)
    {
        var voiced = 0;

        for (var start = 0; start + VoiceWindowSamples <= window.Length; start += VoiceWindowSamples)
        {
            double sum = 0;
            foreach (float sample in window.Slice(start, VoiceWindowSamples))
            {
                sum += (double)sample * sample;
            }

            if (Math.Sqrt(sum / VoiceWindowSamples) >= VoiceFloor)
            {
                voiced += VoiceWindowSamples;
            }
        }

        return voiced;
    }

    /// <summary>
    /// One window the model choked on is not worth losing the other several hundred, and certainly
    /// not worth losing the transcript being written alongside them.
    /// </summary>
    private float[] Embed(ReadOnlySpan<float> window)
    {
        try
        {
            return _embedder is null ? [] : _embedder.Embed(window);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static int ToSamples(TimeSpan offset) =>
        (int)Math.Round(offset.TotalSeconds * AudioFrame.SampleRate);
}

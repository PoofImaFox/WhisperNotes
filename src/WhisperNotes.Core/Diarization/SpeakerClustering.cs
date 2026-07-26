using System.Numerics;

namespace WhisperNotes.Core.Diarization;

/// <summary>
/// Groups per-utterance speaker embeddings into voices without being told how many there are.
/// </summary>
/// <remarks>
/// Average-linkage agglomerative clustering over cosine distance, cut at a fixed height. Average
/// linkage is chosen over centroid linkage for two reasons: it is the standard for this problem, and
/// it is <em>reducible</em>, meaning a merge can never bring two clusters closer together than they
/// already were. That rules out dendrogram inversions, which in turn is what makes cutting at a
/// height well-defined and lets the nearest-neighbour chain below run in quadratic time instead of
/// cubic.
/// </remarks>
internal static class SpeakerClustering
{
    /// <summary>
    /// Cosine distance is bounded by 2, so this is "further apart than anything real". Used for
    /// vectors that carry no direction at all, which must never win a nearest-neighbour search.
    /// </summary>
    private const float Unreachable = 4f;

    /// <summary>
    /// Assigns each embedding to a speaker.
    /// </summary>
    /// <param name="embeddings">One L2-normalised vector per observed stretch of speech.</param>
    /// <param name="weights">
    /// How much each stretch counts for, in seconds of audio. Three seconds of someone talking is
    /// better evidence of who they are than half a second of it, and linkage respects that.
    /// </param>
    /// <param name="mergeThreshold">
    /// Cosine distance at which two groups stop being the same voice. Higher lumps more people
    /// together; lower splits one person into several.
    /// </param>
    /// <param name="maxSpeakers">
    /// Ceiling on the answer. This overrides <paramref name="mergeThreshold"/>: a threshold that
    /// would leave twenty speakers in a four-person meeting is worse than useless, so merging
    /// continues past it until the count fits.
    /// </param>
    /// <returns>
    /// A speaker index per embedding, numbered from zero in order of first appearance so that the
    /// first thing said in a recording is always Speaker 1.
    /// </returns>
    public static int[] Cluster(
        IReadOnlyList<float[]> embeddings,
        IReadOnlyList<double> weights,
        double mergeThreshold,
        int maxSpeakers)
    {
        int count = embeddings?.Count ?? 0;
        if (count == 0 || embeddings is null)
        {
            return [];
        }

        if (count == 1)
        {
            return [0];
        }

        maxSpeakers = Math.Max(1, maxSpeakers);

        float[][] directions = Directions(embeddings, out bool[] usable);
        double[] mass = Mass(weights, count);
        float[] distances = Distances(directions, usable);

        return Assign(Agglomerate(distances, mass, count), count, mergeThreshold, maxSpeakers);
    }

    /// <summary>
    /// Re-normalises defensively rather than trusting the caller, and flags the vectors that cannot
    /// be normalised at all. A silent NaN here would propagate into every distance and quietly
    /// decide the whole clustering.
    /// </summary>
    private static float[][] Directions(IReadOnlyList<float[]> embeddings, out bool[] usable)
    {
        int count = embeddings.Count;

        // The width is taken from the first real vector, and anything of a different width is
        // dropped rather than truncated to fit. Taking the minimum width instead would let one
        // empty vector — which is exactly what the embedder returns for a window too short to
        // decode — silently flatten every other vector to nothing and quietly decide the whole
        // clustering, which is far worse than ignoring the one odd entry.
        int dimensions = 0;
        foreach (float[] embedding in embeddings)
        {
            if (embedding is { Length: > 0 })
            {
                dimensions = embedding.Length;
                break;
            }
        }

        float[][] directions = new float[count][];
        usable = new bool[count];

        for (int i = 0; i < count; i++)
        {
            float[] source = embeddings[i] ?? [];
            float[] unit = new float[dimensions];
            double sum = source.Length == dimensions ? 0 : double.NaN;

            for (int d = 0; d < dimensions && !double.IsNaN(sum); d++)
            {
                float value = source[d];
                if (!float.IsFinite(value))
                {
                    sum = double.NaN;
                    break;
                }

                unit[d] = value;
                sum += (double)value * value;
            }

            double norm = Math.Sqrt(sum);
            if (!double.IsFinite(norm) || norm <= 0)
            {
                directions[i] = new float[dimensions];
                continue;
            }

            float scale = (float)(1 / norm);
            for (int d = 0; d < dimensions; d++)
            {
                unit[d] *= scale;
            }

            directions[i] = unit;
            usable[i] = true;
        }

        return directions;
    }

    /// <summary>
    /// A weight that is missing, negative or not a number says nothing about how long someone spoke,
    /// so it falls back to counting the stretch once rather than sinking the whole clustering.
    /// </summary>
    private static double[] Mass(IReadOnlyList<double>? weights, int count)
    {
        double[] mass = new double[count];

        for (int i = 0; i < count; i++)
        {
            double weight = weights is not null && i < weights.Count ? weights[i] : 1;
            mass[i] = double.IsFinite(weight) && weight > 0 ? weight : 1;
        }

        return mass;
    }

    /// <summary>
    /// The condensed upper triangle of the pairwise cosine-distance matrix. Quadratic in the number
    /// of stretches, which is why the caller bounds how many it observes.
    /// </summary>
    private static float[] Distances(float[][] directions, bool[] usable)
    {
        int count = directions.Length;
        float[] distances = new float[count * (count - 1) / 2];
        int at = 0;

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                distances[at++] = usable[i] && usable[j]
                    ? 1f - Dot(directions[i], directions[j])
                    : Unreachable;
            }
        }

        return distances;
    }

    private static float Dot(float[] left, float[] right)
    {
        float total = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated && left.Length >= Vector<float>.Count)
        {
            Vector<float> accumulator = Vector<float>.Zero;
            int limit = left.Length - Vector<float>.Count;

            for (; i <= limit; i += Vector<float>.Count)
            {
                accumulator += new Vector<float>(left, i) * new Vector<float>(right, i);
            }

            total = Vector.Sum(accumulator);
        }

        for (; i < left.Length; i++)
        {
            total += left[i] * right[i];
        }

        return total;
    }

    /// <summary>
    /// Builds the full dendrogram with the nearest-neighbour chain algorithm: walk a chain of
    /// mutual nearest neighbours and merge whenever the chain doubles back on itself. Every cluster
    /// is visited a bounded number of times, so this stays quadratic where the naive
    /// "rescan for the global minimum after every merge" would be cubic.
    /// </summary>
    private static Merge[] Agglomerate(float[] distances, double[] mass, int count)
    {
        bool[] alive = new bool[count];
        Array.Fill(alive, true);

        int[] chain = new int[count + 1];
        int depth = 0;

        Merge[] merges = new Merge[count - 1];
        int merged = 0;

        while (merged < count - 1)
        {
            if (depth == 0)
            {
                // Restarting scans from zero rather than from wherever the last chain began: a
                // merge keeps the lower of the two indices, so survivors accumulate behind any
                // running cursor and a forward-only search would walk off the end.
                int start = 0;
                while (!alive[start])
                {
                    start++;
                }

                chain[depth++] = start;
            }

            int a = chain[depth - 1];
            int b = Nearest(distances, alive, count, a, out float best);

            // Doubling back means a and b are each other's nearest: no other pair can be closer,
            // which is exactly the pair average linkage wants to merge next.
            if (depth >= 2 && b == chain[depth - 2])
            {
                depth -= 2;

                int keep = Math.Min(a, b);
                int drop = Math.Max(a, b);

                merges[merged++] = new Merge(keep, drop, best);
                Absorb(distances, mass, alive, count, keep, drop);
            }
            else
            {
                chain[depth++] = b;
            }
        }

        // Reducibility keeps the tree itself free of inversions, but it says nothing about the
        // order merges are discovered in: the chain merges whichever mutual pair it happens to be
        // standing next to, which is rarely the closest pair left anywhere. A between-speaker join
        // can therefore surface long before the within-speaker joins it towers over, and the cut
        // below reads the list as a height ordering rather than a tree. Sorting is what makes the
        // two agree; it is stable, so a cluster still appears before the merge that consumes it.
        return [.. merges.OrderBy(merge => merge.Height)];
    }

    /// <summary>Closest live cluster to <paramref name="from"/>, ties going to the lowest index.</summary>
    private static int Nearest(float[] distances, bool[] alive, int count, int from, out float best)
    {
        best = float.PositiveInfinity;
        int nearest = -1;

        for (int other = 0; other < count; other++)
        {
            if (other == from || !alive[other])
            {
                continue;
            }

            float distance = distances[Index(count, from, other)];
            if (distance < best)
            {
                best = distance;
                nearest = other;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Folds <paramref name="drop"/> into <paramref name="keep"/>, updating every distance by the
    /// Lance-Williams rule for average linkage: the new distance to a third cluster is the mass
    /// weighted mean of the two old ones.
    /// </summary>
    private static void Absorb(float[] distances, double[] mass, bool[] alive, int count, int keep, int drop)
    {
        double total = mass[keep] + mass[drop];

        for (int other = 0; other < count; other++)
        {
            if (other == keep || other == drop || !alive[other])
            {
                continue;
            }

            int left = Index(count, keep, other);
            int right = Index(count, drop, other);

            distances[left] = (float)((mass[keep] * distances[left] + mass[drop] * distances[right]) / total);
        }

        mass[keep] = total;
        alive[drop] = false;
    }

    /// <summary>
    /// Cuts the dendrogram and renumbers what falls out. Merges arrive in non-decreasing height,
    /// so walking them in order and stopping is a clean horizontal cut.
    /// </summary>
    private static int[] Assign(Merge[] merges, int count, double mergeThreshold, int maxSpeakers)
    {
        int[] parent = new int[count];
        for (int i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        int clusters = count;

        foreach (Merge merge in merges)
        {
            // The ceiling outranks the threshold: too many speakers is a worse answer than a
            // slightly over-merged one, so keep going until the count fits either way.
            if (clusters <= maxSpeakers && merge.Height > mergeThreshold)
            {
                break;
            }

            int left = Find(parent, merge.Keep);
            int right = Find(parent, merge.Drop);
            if (left == right)
            {
                continue;
            }

            parent[right] = left;
            clusters--;
        }

        int[] speakers = new int[count];
        Dictionary<int, int> numbering = new(clusters);

        for (int i = 0; i < count; i++)
        {
            int root = Find(parent, i);
            if (!numbering.TryGetValue(root, out int speaker))
            {
                speaker = numbering.Count;
                numbering[root] = speaker;
            }

            speakers[i] = speaker;
        }

        return speakers;
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    /// <summary>Position of the (i, j) pair in the condensed upper triangle, in either order.</summary>
    private static int Index(int count, int i, int j)
    {
        if (i > j)
        {
            (i, j) = (j, i);
        }

        return (i * (2 * count - i - 1) / 2) + (j - i - 1);
    }

    private readonly record struct Merge(int Keep, int Drop, double Height);
}

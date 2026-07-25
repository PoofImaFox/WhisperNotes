namespace NoteScribe.Core.Notes.Documents;

/// <summary>What happened to a line between two versions of a document.</summary>
public enum DiffKind
{
    /// <summary>Present in both versions.</summary>
    Unchanged,

    /// <summary>Only in the right-hand (newer) version.</summary>
    Added,

    /// <summary>Only in the left-hand (older) version.</summary>
    Removed
}

/// <summary>One line of a rendered diff.</summary>
/// <param name="Kind">Whether the line was added, removed, or is common to both sides.</param>
/// <param name="Text">The line itself, without its newline.</param>
/// <param name="LeftLine">1-based line number on the left, or null for an added line.</param>
/// <param name="RightLine">1-based line number on the right, or null for a removed line.</param>
public sealed record DiffLine(DiffKind Kind, string Text, int? LeftLine, int? RightLine);

/// <summary>
/// Line-level text diff. Pure, allocation-conscious, no IO and no external package.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b> Myers' O(N·D) difference algorithm in its linear-space divide-and-conquer form
/// (the "middle snake" bisection from §4b of the paper): each call locates a point where a shortest
/// edit path crosses the middle of the edit graph and recurses on the two halves, so working memory
/// is O(N+M) rather than the O(N·M) of a full LCS matrix. A 10k-line note therefore costs kilobytes,
/// not hundreds of megabytes.
/// </para>
/// <para>
/// Three things keep it fast in the editor. Common prefixes and suffixes are stripped before any
/// search, which reduces a keystroke-sized edit to a handful of lines. Lines are interned to
/// integers once per call so the inner loops compare <c>int</c>s, and the interning probes the table
/// by <c>ReadOnlySpan&lt;char&gt;</c> so only lines never seen before are materialised as strings.
/// And when the edit distance in a region exceeds <c>max(1024, 8·sqrt(N+M))</c> the search stops and
/// splits at the furthest-reaching path found so far — git's heuristic. The result stays a correct
/// diff, just not a provably minimal one, and the worst case drops from O((N+M)^2) to roughly
/// O((N+M)^1.5). Realistic note edits are far below the threshold and come out exactly minimal.
/// </para>
/// <para>
/// <b>Normalisation.</b> Both sides have <c>\r\n</c> and lone <c>\r</c> collapsed to <c>\n</c>, and a
/// single trailing newline is not treated as an extra empty line — so "a\n" and "a" diff clean.
/// </para>
/// </remarks>
public static class TextDiff
{
    /// <summary>Below this the exact Myers search always runs to completion.</summary>
    private const int MinEditCostCeiling = 1024;

    /// <summary>Guards against a pathological split chain blowing the stack.</summary>
    private const int MaxRecursionDepth = 100;

    /// <summary>
    /// The full aligned diff: every line of both versions, in order, with removals of a change
    /// block listed before its additions.
    /// </summary>
    public static IReadOnlyList<DiffLine> Lines(string left, string right)
    {
        var (l, r) = BuildPair(left, right);
        var runs = Align(l.Ids, r.Ids);

        var result = new List<DiffLine>(l.Count + r.Count);
        var a = 0;
        var b = 0;

        foreach (var run in runs)
        {
            EmitChanged(result, l, r, a, run.A, b, run.B);

            for (var k = 0; k < run.Length; k++)
            {
                result.Add(new DiffLine(DiffKind.Unchanged, l.Line(run.A + k), run.A + k + 1, run.B + k + 1));
            }

            a = run.A + run.Length;
            b = run.B + run.Length;
        }

        EmitChanged(result, l, r, a, l.Count, b, r.Count);
        return result;
    }

    /// <summary>
    /// The compact view the revert UI renders: only changes plus <paramref name="context"/> lines of
    /// surrounding text. Unchanged runs longer than <c>2 * context</c> have their middle dropped, and
    /// two identical inputs produce an empty list.
    /// </summary>
    /// <remarks>
    /// Elisions are not represented by an entry — they show up as a jump in
    /// <see cref="DiffLine.LeftLine"/> / <see cref="DiffLine.RightLine"/> between consecutive
    /// results. Use <see cref="IsElision"/> to place a separator.
    /// </remarks>
    public static IReadOnlyList<DiffLine> Unified(string left, string right, int context = 3)
    {
        if (context < 0)
        {
            context = 0;
        }

        var all = Lines(left, right);
        if (all.Count == 0)
        {
            return all;
        }

        var keep = new bool[all.Count];
        var changed = false;

        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Kind == DiffKind.Unchanged)
            {
                continue;
            }

            changed = true;
            keep[i] = true;

            // Widening around every change makes the boundaries fall exactly `context` lines out
            // from the nearest change on each side, so a kept run is never ragged.
            for (var j = Math.Max(0, i - context); j < i; j++)
            {
                keep[j] = true;
            }

            var end = Math.Min(all.Count - 1, i + context);
            for (var j = i + 1; j <= end; j++)
            {
                keep[j] = true;
            }
        }

        if (!changed)
        {
            return [];
        }

        var result = new List<DiffLine>();
        for (var i = 0; i < all.Count; i++)
        {
            if (keep[i])
            {
                result.Add(all[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Added and removed line counts — the "+12 −3" badge. Materialises no line strings, so it is
    /// cheap enough to call on every keystroke debounce.
    /// </summary>
    public static (int Added, int Removed) Stat(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return (0, 0);
        }

        var (l, r) = BuildPair(left, right);
        var common = 0;
        foreach (var run in Align(l.Ids, r.Ids))
        {
            common += run.Length;
        }

        return (r.Count - common, l.Count - common);
    }

    /// <summary>
    /// True when <see cref="Unified"/> dropped lines between two consecutive results, i.e. the
    /// renderer should draw a "…" separator. Exact for <c>context &gt;= 1</c>.
    /// </summary>
    public static bool IsElision(DiffLine previous, DiffLine current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (previous.LeftLine is { } pl && current.LeftLine is { } cl && cl > pl + 1)
        {
            return true;
        }

        return previous.RightLine is { } pr && current.RightLine is { } cr && cr > pr + 1;
    }

    private static void EmitChanged(
        List<DiffLine> result,
        in LineIndex left,
        in LineIndex right,
        int aFrom,
        int aTo,
        int bFrom,
        int bTo)
    {
        // Unified-diff convention: the block's removals, then its additions.
        for (var i = aFrom; i < aTo; i++)
        {
            result.Add(new DiffLine(DiffKind.Removed, left.Line(i), i + 1, null));
        }

        for (var j = bFrom; j < bTo; j++)
        {
            result.Add(new DiffLine(DiffKind.Added, right.Line(j), null, j + 1));
        }
    }

    // ---- line indexing -------------------------------------------------------------------------

    private static (LineIndex Left, LineIndex Right) BuildPair(string? left, string? right)
    {
        // One shared table so equal lines get equal ids across the two sides.
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        return (Index(Normalize(left), map), Index(Normalize(right), map));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains('\r', StringComparison.Ordinal)
            ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : value;
    }

    private static LineIndex Index(string text, Dictionary<string, int> map)
    {
        if (text.Length == 0)
        {
            return new LineIndex(text, [], [], []);
        }

        // A single trailing newline terminates the last line rather than starting an empty one, so
        // "a\n" and "a" produce the same lines and never show a phantom change.
        var breaks = text.AsSpan().Count('\n');
        var count = text[^1] == '\n' ? breaks : breaks + 1;

        var starts = new int[count];
        var lengths = new int[count];
        var ids = new int[count];

        var lookup = map.GetAlternateLookup<ReadOnlySpan<char>>();
        var position = 0;

        for (var i = 0; i < count; i++)
        {
            var next = text.IndexOf('\n', position);
            var end = next < 0 ? text.Length : next;
            var span = text.AsSpan(position, end - position);

            if (!lookup.TryGetValue(span, out var id))
            {
                // Only a line never seen before costs a string allocation.
                id = map.Count;
                map[span.ToString()] = id;
            }

            starts[i] = position;
            lengths[i] = end - position;
            ids[i] = id;
            position = end + 1;
        }

        return new LineIndex(text, starts, lengths, ids);
    }

    // ---- Myers ---------------------------------------------------------------------------------

    private static List<CommonRun> Align(int[] a, int[] b)
    {
        var runs = new List<CommonRun>();
        Walk(a, 0, a.Length, b, 0, b.Length, runs, 0);
        return runs;
    }

    /// <summary>
    /// Appends the common runs between <c>a[aLo..aHi)</c> and <c>b[bLo..bHi)</c>, in order.
    /// Everything not covered by a run is an edit.
    /// </summary>
    private static void Walk(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, List<CommonRun> runs, int depth)
    {
        var prefix = 0;
        while (aLo + prefix < aHi && bLo + prefix < bHi && a[aLo + prefix] == b[bLo + prefix])
        {
            prefix++;
        }

        if (prefix > 0)
        {
            runs.Add(new CommonRun(aLo, bLo, prefix));
            aLo += prefix;
            bLo += prefix;
        }

        var suffix = 0;
        while (aHi - suffix - 1 >= aLo && bHi - suffix - 1 >= bLo && a[aHi - suffix - 1] == b[bHi - suffix - 1])
        {
            suffix++;
        }

        aHi -= suffix;
        bHi -= suffix;

        // When either side of the middle is empty the region is a pure insertion or deletion and
        // holds nothing in common — nothing to record.
        if (aLo < aHi && bLo < bHi)
        {
            var n = aHi - aLo;
            var m = bHi - bLo;

            var (x, y) = depth >= MaxRecursionDepth
                ? (aLo + Math.Max(1, n / 2), bLo + (m / 2)) // Bail to a halving split; still a valid decomposition.
                : FindSplit(a, aLo, aHi, b, bLo, bHi);

            // Termination guard: a split that does not shrink the region would recurse forever.
            // Falling back to (aHi, bLo) makes both halves pure edits, which bottom out immediately.
            if (x < aLo || x > aHi || y < bLo || y > bHi || (x == aLo && y == bLo) || (x == aHi && y == bHi))
            {
                x = aHi;
                y = bLo;
            }

            Walk(a, aLo, x, b, bLo, y, runs, depth + 1);
            Walk(a, x, aHi, b, y, bHi, runs, depth + 1);
        }

        if (suffix > 0)
        {
            runs.Add(new CommonRun(aHi, bHi, suffix));
        }
    }

    /// <summary>
    /// Finds a point where a shortest edit path crosses the middle of the region's edit graph, by
    /// running the greedy Myers search forwards from the top-left and backwards from the
    /// bottom-right until the two frontiers overlap.
    /// </summary>
    /// <returns>Absolute indices into <paramref name="a"/> and <paramref name="b"/> to split at.</returns>
    private static (int X, int Y) FindSplit(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi)
    {
        var n = aHi - aLo;
        var m = bHi - bLo;

        var maxD = ((n + m) + 1) / 2;
        var offset = maxD;
        var length = (2 * maxD) + 2;

        var forward = new int[length];
        var backward = new int[length];
        Array.Fill(forward, -1);
        Array.Fill(backward, -1);
        forward[offset + 1] = 0;
        backward[offset + 1] = 0;

        var delta = n - m;
        var front = (delta & 1) != 0;

        int fStart = 0, fEnd = 0, bStart = 0, bEnd = 0;

        var ceiling = Math.Max(MinEditCostCeiling, (int)Math.Sqrt(n + m) * 8);
        int bestX = -1, bestY = -1, bestReach = -1;

        for (var d = 0; d < maxD; d++)
        {
            for (var k = -d + fStart; k <= d - fEnd; k += 2)
            {
                var ko = offset + k;
                var x = k == -d || (k != d && forward[ko - 1] < forward[ko + 1])
                    ? forward[ko + 1]
                    : forward[ko - 1] + 1;
                var y = x - k;

                while (x < n && y < m && a[aLo + x] == b[bLo + y])
                {
                    x++;
                    y++;
                }

                forward[ko] = x;

                // Remember the furthest-reaching interior point in case the cost ceiling is hit.
                if (x > 0 && y > 0 && x < n && y < m && x + y > bestReach)
                {
                    bestReach = x + y;
                    bestX = x;
                    bestY = y;
                }

                if (x > n)
                {
                    fEnd += 2;
                }
                else if (y > m)
                {
                    fStart += 2;
                }
                else if (front)
                {
                    var mirror = offset + delta - k;
                    if (mirror >= 0 && mirror < length && backward[mirror] != -1 && x >= n - backward[mirror])
                    {
                        return (aLo + x, bLo + y);
                    }
                }
            }

            for (var k = -d + bStart; k <= d - bEnd; k += 2)
            {
                var ko = offset + k;
                var x = k == -d || (k != d && backward[ko - 1] < backward[ko + 1])
                    ? backward[ko + 1]
                    : backward[ko - 1] + 1;
                var y = x - k;

                while (x < n && y < m && a[aHi - x - 1] == b[bHi - y - 1])
                {
                    x++;
                    y++;
                }

                backward[ko] = x;

                if (x > n)
                {
                    bEnd += 2;
                }
                else if (y > m)
                {
                    bStart += 2;
                }
                else if (!front)
                {
                    var mirror = offset + delta - k;
                    if (mirror >= 0 && mirror < length && forward[mirror] != -1)
                    {
                        var fx = forward[mirror];
                        if (fx >= n - x)
                        {
                            return (aLo + fx, bLo + (offset + fx - mirror));
                        }
                    }
                }
            }

            if (d > ceiling && bestX > 0)
            {
                // Give up on minimality rather than on responsiveness. Any reachable interior point
                // decomposes the problem correctly; this one is the best found so far.
                return (aLo + bestX, bLo + bestY);
            }
        }

        // Frontiers never met: the region has nothing in common, so treat it as a wholesale
        // replacement — all of the left deleted, then all of the right added.
        return (aHi, bLo);
    }

    /// <summary>A maximal stretch of lines present in both versions.</summary>
    private readonly record struct CommonRun(int A, int B, int Length);

    /// <summary>
    /// A normalised text plus the offsets of its lines and their interned ids. Keeping lines as
    /// (offset, length) pairs means <see cref="Stat"/> never allocates a single line string.
    /// </summary>
    private readonly struct LineIndex(string text, int[] starts, int[] lengths, int[] ids)
    {
        public int[] Ids { get; } = ids;

        public int Count => Ids.Length;

        public string Line(int index) =>
            lengths[index] == 0 ? string.Empty : text.Substring(starts[index], lengths[index]);
    }
}

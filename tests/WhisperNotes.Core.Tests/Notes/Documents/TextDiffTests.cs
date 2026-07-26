using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WhisperNotes.Core.Notes.Documents;

namespace WhisperNotes.Core.Tests.Notes.Documents;

/// <summary>
/// Characterisation tests for the Myers diff.
/// </summary>
/// <remarks>
/// The invariant tests say the output is <em>a</em> correct minimal diff; the digest test says it is
/// the <em>same</em> one it has always been. Both matter, because a shortest edit script is not
/// unique — a refactor of the search can stay minimal and still start choosing a different path,
/// which the reader of a revert dialog would see as the diff moving around underneath them.
/// </remarks>
public sealed class TextDiffTests
{
    [Fact]
    public void Lines_ReportsNoChange_WhenBothSidesMatch()
    {
        Assert.Equal(
            " 1/1 alpha\n 2/2 beta\n",
            Render(TextDiff.Lines("alpha\nbeta", "alpha\nbeta")));
    }

    [Fact]
    public void Lines_IgnoresTrailingNewlineAndLineEndingStyle()
    {
        Assert.Equal(" 1/1 alpha\n 2/2 beta\n", Render(TextDiff.Lines("alpha\nbeta\n", "alpha\nbeta")));
        Assert.Equal(" 1/1 alpha\n 2/2 beta\n", Render(TextDiff.Lines("alpha\r\nbeta", "alpha\nbeta")));
        Assert.Equal(" 1/1 alpha\n 2/2 beta\n", Render(TextDiff.Lines("alpha\rbeta", "alpha\nbeta")));
    }

    [Fact]
    public void Lines_ListsRemovalsBeforeAdditionsOfAChangeBlock()
    {
        Assert.Equal(
            " 1/1 a\n-2/. old\n-3/. older\n+./2 new\n 4/3 z\n",
            Render(TextDiff.Lines("a\nold\nolder\nz", "a\nnew\nz")));
    }

    [Fact]
    public void Lines_HandlesPureInsertionAndPureDeletion()
    {
        Assert.Equal(" 1/1 a\n+./2 b\n 2/3 c\n", Render(TextDiff.Lines("a\nc", "a\nb\nc")));
        Assert.Equal(" 1/1 a\n-2/. b\n 3/2 c\n", Render(TextDiff.Lines("a\nb\nc", "a\nc")));
        Assert.Equal("+./1 a\n+./2 b\n", Render(TextDiff.Lines(string.Empty, "a\nb")));
        Assert.Equal("-1/. a\n-2/. b\n", Render(TextDiff.Lines("a\nb", string.Empty)));
    }

    [Fact]
    public void Stat_CountsAddedAndRemovedLines()
    {
        Assert.Equal((0, 0), TextDiff.Stat("a\nb", "a\nb"));
        Assert.Equal((1, 2), TextDiff.Stat("a\nold\nolder\nz", "a\nnew\nz"));
    }

    [Fact]
    public void Unified_KeepsOnlyChangesAndTheirContext()
    {
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var right = left.Replace("\n10\n", "\nten\n", StringComparison.Ordinal);

        var unified = TextDiff.Unified(left, right, context: 2);

        Assert.Equal(
            [" 8", " 9", "-10", "+ten", " 11", " 12"],
            unified.Select(line => Marker(line.Kind) + line.Text));
    }

    [Fact]
    public void Unified_IsEmpty_WhenNothingChanged()
    {
        Assert.Empty(TextDiff.Unified("a\nb\nc", "a\nb\nc"));
    }

    [Fact]
    public void IsElision_DetectsTheGapUnifiedLeftBehind()
    {
        var left = string.Join('\n', Enumerable.Range(1, 40).Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var right = left.Replace("\n5\n", "\nfive\n", StringComparison.Ordinal)
            .Replace("\n35\n", "\nthirtyfive\n", StringComparison.Ordinal);

        var unified = TextDiff.Unified(left, right, context: 1);
        var elisions = 0;
        for (var i = 1; i < unified.Count; i++)
        {
            if (TextDiff.IsElision(unified[i - 1], unified[i]))
            {
                elisions++;
            }
        }

        Assert.Equal(1, elisions);
    }

    /// <summary>
    /// Every line of both inputs appears exactly once and in order, so the rendered diff can always
    /// be collapsed back into either version.
    /// </summary>
    [Fact]
    public void Lines_ReconstructsBothSides_AcrossTheCorpus()
    {
        foreach (var (left, right) in Corpus())
        {
            var diff = TextDiff.Lines(left, right);

            Assert.Equal(
                Split(left),
                diff.Where(line => line.Kind != DiffKind.Added).Select(line => line.Text));
            Assert.Equal(
                Split(right),
                diff.Where(line => line.Kind != DiffKind.Removed).Select(line => line.Text));
        }
    }

    /// <summary>Line numbers must be dense and 1-based on each side, or the revert UI mis-targets.</summary>
    [Fact]
    public void Lines_NumbersEachSideConsecutively_AcrossTheCorpus()
    {
        foreach (var (left, right) in Corpus())
        {
            var nextLeft = 1;
            var nextRight = 1;

            foreach (var line in TextDiff.Lines(left, right))
            {
                Assert.Equal(line.Kind != DiffKind.Added, line.LeftLine is not null);
                Assert.Equal(line.Kind != DiffKind.Removed, line.RightLine is not null);

                if (line.LeftLine is { } l)
                {
                    Assert.Equal(nextLeft++, l);
                }

                if (line.RightLine is { } r)
                {
                    Assert.Equal(nextRight++, r);
                }
            }
        }
    }

    /// <summary>
    /// The corpus stays far below the cost ceiling that trades minimality for responsiveness, so the
    /// edit script must be exactly as short as a full LCS says it can be.
    /// </summary>
    [Fact]
    public void Stat_IsMinimal_AcrossTheCorpus()
    {
        foreach (var (left, right) in Corpus())
        {
            var a = Split(left);
            var b = Split(right);
            var common = LongestCommonSubsequence(a, b);

            Assert.Equal((b.Length - common, a.Length - common), TextDiff.Stat(left, right));
        }
    }

    /// <summary>
    /// Pins the exact edit path chosen for the whole corpus. Recorded from the implementation as it
    /// stood before the middle-snake search was split by direction; a change here means the diff
    /// moved, even if it is still minimal.
    /// </summary>
    [Fact]
    public void Lines_ProducesTheRecordedEditPath_AcrossTheCorpus()
    {
        var builder = new StringBuilder();
        foreach (var (left, right) in Corpus())
        {
            builder.Append(Render(TextDiff.Lines(left, right))).Append("==").Append('\n');
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));

        Assert.Equal("f33a670d95e1f1dc528c07448b71d6ecd88df81e1221e780691050084f72b301", digest);
    }

    private static string Render(IReadOnlyList<DiffLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder
                .Append(Marker(line.Kind))
                .Append(line.LeftLine?.ToString(CultureInfo.InvariantCulture) ?? ".")
                .Append('/')
                .Append(line.RightLine?.ToString(CultureInfo.InvariantCulture) ?? ".")
                .Append(' ')
                .Append(line.Text)
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string Marker(DiffKind kind) => kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "-",
        _ => " ",
    };

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    /// <summary>Reference O(N·M) LCS — the definition the linear-space search has to agree with.</summary>
    private static int LongestCommonSubsequence(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        return table[0, 0];
    }

    /// <summary>
    /// Fixed-seed pairs: unrelated texts to exercise the search itself, and mutations of a common
    /// base to exercise the prefix/suffix trimming that real editing hits.
    /// </summary>
    private static IEnumerable<(string Left, string Right)> Corpus()
    {
        var random = new Random(20260725);

        for (var i = 0; i < 120; i++)
        {
            yield return (RandomText(random, 20), RandomText(random, 20));
        }

        for (var i = 0; i < 120; i++)
        {
            var basis = RandomText(random, 30);
            yield return (basis, Mutate(random, basis));
        }
    }

    private static string RandomText(Random random, int maximumLines)
    {
        var builder = new StringBuilder();
        var count = random.Next(0, maximumLines + 1);
        for (var i = 0; i < count; i++)
        {
            builder.Append((char)('a' + random.Next(5))).Append('\n');
        }

        return builder.ToString();
    }

    private static string Mutate(Random random, string text)
    {
        var lines = new List<string>(Split(text));
        var edits = random.Next(0, 7);

        for (var edit = 0; edit < edits; edit++)
        {
            var line = ((char)('a' + random.Next(5))).ToString();
            switch (random.Next(3))
            {
                case 0:
                    lines.Insert(random.Next(lines.Count + 1), line);
                    break;
                case 1 when lines.Count > 0:
                    lines.RemoveAt(random.Next(lines.Count));
                    break;
                case 2 when lines.Count > 0:
                    lines[random.Next(lines.Count)] = line;
                    break;
                default:
                    lines.Add(line);
                    break;
            }
        }

        return lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
    }
}

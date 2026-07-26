using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using WhisperNotes.Core.Notes.Documents;
using WhisperNotes.Core.Notes.Exporting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace WhisperNotes.Core.Tests.Notes.Exporting;

public sealed class NoteExportServiceTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly NoteExportService _service = new();

    [Fact]
    public void Export_Markdown_PreservesBodyAsBomlessUtf8()
    {
        const string content = "# Café ☕\n\nFirst line.\n\n- α\n- β\n";
        NoteDocument note = CreateNote(title: "Sprint notes", content: content);

        NoteExportArtifact artifact = _service.Export(note, NoteExportFormat.Markdown);

        Assert.Equal("text/markdown", MediaType(artifact.ContentType));
        Assert.EndsWith(".md", artifact.SuggestedFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, DecodeUtf8WithoutBom(artifact.Content));
        Assert.Equal(StrictUtf8.GetBytes(content), artifact.Content);
    }

    [Fact]
    public void Export_Markdown_UsesTraversalResistantFileName()
    {
        NoteDocument note = CreateNote(title: @"..\..\CON:<draft>?*|""/meeting");

        NoteExportArtifact artifact = _service.Export(note, NoteExportFormat.Markdown);

        AssertSafeStandaloneFileName(artifact.SuggestedFileName, ".md");
    }

    [Fact]
    public void Export_Html_ProducesCompleteDocumentAndRendersMarkdown()
    {
        NoteDocument note = CreateNote(
            title: "Release notes",
            content: "# Agenda\n\nThis is **approved**.\n\n- One\n- Two");

        NoteExportArtifact artifact = _service.Export(note, NoteExportFormat.Html);
        string html = DecodeUtf8WithoutBom(artifact.Content);

        Assert.Equal("text/html", MediaType(artifact.ContentType));
        AssertSafeStandaloneFileName(artifact.SuggestedFileName, ".html");
        Assert.Contains("<!DOCTYPE html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<head", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<body", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</body>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex("<h1\\b[^>]*>\\s*Agenda\\s*</h1>",
            RegexOptions.IgnoreCase), html);
        Assert.Matches(new Regex("<strong\\b[^>]*>\\s*approved\\s*</strong>",
            RegexOptions.IgnoreCase), html);
        Assert.Matches(new Regex("<li\\b[^>]*>\\s*One\\s*</li>",
            RegexOptions.IgnoreCase), html);
    }

    [Fact]
    public void Export_Html_EscapesUntrustedTitleAndBodyHtml()
    {
        const string dangerousTitle = "<img src=x onerror=alert(1)> & Planning";
        const string dangerousBody = """
            # Safe heading

            <script>alert("body")</script>

            Text & more text.
            """;
        NoteDocument note = CreateNote(title: dangerousTitle, content: dangerousBody);

        string html = DecodeUtf8WithoutBom(
            _service.Export(note, NoteExportFormat.Html).Content);

        Assert.DoesNotContain(dangerousTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&amp; Planning", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Text &amp; more text.", html, StringComparison.Ordinal);
        Assert.Contains("<h1", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_Pdf_ProducesStructurallyCompletePdf()
    {
        NoteDocument note = CreateNote(
            title: "Architecture review",
            content: "# Decisions\n\nA real body with punctuation, numbers 123, and Unicode Ω.");

        NoteExportArtifact artifact = _service.Export(note, NoteExportFormat.Pdf);

        Assert.Equal("application/pdf", MediaType(artifact.ContentType));
        AssertSafeStandaloneFileName(artifact.SuggestedFileName, ".pdf");
        Assert.True(artifact.Content.Length > 500,
            $"Expected a non-trivial PDF, but it was only {artifact.Content.Length} bytes.");

        string pdf = Encoding.Latin1.GetString(artifact.Content);
        Assert.StartsWith("%PDF-", pdf, StringComparison.Ordinal);
        Assert.Contains("startxref", pdf, StringComparison.Ordinal);
        Assert.Matches(new Regex("%%EOF\\s*$", RegexOptions.CultureInvariant), pdf);
        Assert.True(CountPdfPages(pdf) >= 1, "The PDF does not declare a page object.");

        using MemoryStream inspectionStream = new(artifact.Content, writable: false);
        using PdfDocument inspected = PdfReader.Open(
            inspectionStream,
            PdfDocumentOpenMode.Import);
        Assert.True(inspected.PageCount >= 1);
        Assert.Equal(note.Title, inspected.Info.Title);
    }

    [Fact]
    public void Export_Pdf_PaginatesLongContent()
    {
        string content = string.Join(
            '\n',
            Enumerable.Range(1, 500).Select(line =>
                $"{line}. This deliberately long line belongs to the exported note."));
        NoteDocument note = CreateNote(title: "Long report", content: content);

        NoteExportArtifact artifact = _service.Export(note, NoteExportFormat.Pdf);
        string pdf = Encoding.Latin1.GetString(artifact.Content);

        Assert.True(artifact.Content.Length > 2_000,
            $"Expected a non-trivial multipage PDF, but it was {artifact.Content.Length} bytes.");
        Assert.True(CountPdfPages(pdf) > 1,
            "A 500-line note should span more than one PDF page.");

        using MemoryStream inspectionStream = new(artifact.Content, writable: false);
        using PdfDocument inspected = PdfReader.Open(
            inspectionStream,
            PdfDocumentOpenMode.Import);
        Assert.True(inspected.PageCount > 1,
            "The PDF library reports that a 500-line note did not paginate.");
    }

    [Fact]
    public void ExportObsidian_CreatesOneMarkdownEntryPerDocumentInProjectFolders()
    {
        NoteDocument first = CreateNote(
            id: "note-1",
            title: "Kickoff",
            project: "Client Alpha",
            content: "# First");
        NoteDocument second = CreateNote(
            id: "note-2",
            title: "Retrospective",
            project: "Client Beta",
            content: "# Second");

        NoteExportArtifact artifact = _service.ExportObsidian([first, second]);
        IReadOnlyDictionary<string, byte[]> entries = ReadZip(artifact);

        Assert.Equal("application/zip", MediaType(artifact.ContentType));
        AssertSafeStandaloneFileName(artifact.SuggestedFileName, ".zip");
        Assert.Equal(2, entries.Count);
        Assert.All(entries.Keys, AssertSafeArchivePath);
        Assert.All(entries.Keys, path =>
            Assert.EndsWith(".md", path, StringComparison.OrdinalIgnoreCase));
        Assert.All(entries.Values, bytes => DecodeUtf8WithoutBom(bytes));
        Assert.Contains(entries.Keys, path => HasFolder(path, "Client Alpha"));
        Assert.Contains(entries.Keys, path => HasFolder(path, "Client Beta"));
    }

    [Fact]
    public void ExportObsidian_UnfiledDocumentUsesFallbackProjectFolder()
    {
        NoteDocument note = CreateNote(title: "Loose note", project: null);

        string path = Assert.Single(ReadZip(_service.ExportObsidian([note]))).Key;
        string[] segments = path.Split('/');

        Assert.Equal(2, segments.Length);
        Assert.False(string.IsNullOrWhiteSpace(segments[0]));
        Assert.Equal("Loose note.md", segments[1]);
    }

    [Fact]
    public void ExportObsidian_WritesMetadataAndPreservesBodyAsBomlessUtf8()
    {
        const string body = "# Héllo 🌍\n\nKeep *all* markdown.\n\n---\n\nIncluding separators.";
        NoteDocument note = CreateNote(
            id: "document-42",
            title: "Field notes",
            project: "Research",
            content: body,
            tags: ["field work", "priority:high"],
            sourceSessionId: "session-99",
            createdUtc: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            updatedUtc: new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));

        byte[] bytes = Assert.Single(ReadZip(_service.ExportObsidian([note]))).Value;
        string markdown = DecodeUtf8WithoutBom(bytes);
        (string frontmatter, string exportedBody) = SplitFrontmatter(markdown);

        AssertMetadata(frontmatter, "title", note.Title);
        AssertMetadata(frontmatter, "project", note.Project!);
        AssertMetadata(frontmatter, "created", "2026-01-02");
        AssertMetadata(frontmatter, "updated", "2026-02-03");
        AssertMetadata(frontmatter, "tags", note.Tags[0]);
        Assert.Contains(note.Tags[1], frontmatter, StringComparison.Ordinal);
        AssertMetadata(frontmatter, "source", note.SourceSessionId!);
        Assert.Equal(body, exportedBody);
    }

    [Fact]
    public void ExportObsidian_SanitizesWeirdAndTraversalLikePaths()
    {
        NoteDocument[] notes =
        [
            CreateNote(id: "one", title: @"..\..\secrets", project: "../../outside"),
            CreateNote(id: "two", title: @"CON:<draft>?*|""", project: @"Client:\West"),
            CreateNote(id: "three", title: "...", project: null),
        ];

        IReadOnlyDictionary<string, byte[]> entries = ReadZip(_service.ExportObsidian(notes));

        Assert.Equal(notes.Length, entries.Count);
        Assert.All(entries.Keys, AssertSafeArchivePath);
        Assert.Equal(entries.Count, entries.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ExportObsidian_UsesDeterministicNumericSuffixesForDuplicateTitles()
    {
        NoteDocument[] notes =
        [
            CreateNote(id: "one", title: "Weekly", project: "Team"),
            CreateNote(id: "two", title: "Weekly", project: "Team"),
            CreateNote(id: "three", title: "Weekly", project: "Team"),
        ];

        string[] firstRun = ReadZip(_service.ExportObsidian(notes)).Keys.ToArray();
        string[] secondRun = ReadZip(_service.ExportObsidian(notes.Reverse().ToArray()))
            .Keys
            .ToArray();

        Assert.Equal(firstRun, secondRun);
        Assert.Equal(3, firstRun.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        string[] stems = firstRun.Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
        Assert.Contains(stems, stem => string.Equals(stem, "Weekly", StringComparison.Ordinal));
        Assert.Contains(stems, stem => HasNumericSuffix(stem, 2));
        Assert.Contains(stems, stem => HasNumericSuffix(stem, 3));
    }

    [Fact]
    public void ExportObsidian_EmptyLibraryProducesValidEmptyZip()
    {
        NoteExportArtifact artifact = _service.ExportObsidian([]);

        Assert.Equal("application/zip", MediaType(artifact.ContentType));
        Assert.Empty(ReadZip(artifact));
    }

    private static NoteDocument CreateNote(
        string id = "note-id",
        string title = "Test note",
        string? project = "Test project",
        string content = "Body",
        IReadOnlyList<string>? tags = null,
        string? sourceSessionId = null,
        DateTimeOffset? createdUtc = null,
        DateTimeOffset? updatedUtc = null) =>
        new(
            id,
            title,
            project,
            content,
            createdUtc ?? new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            updatedUtc ?? new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero),
            tags ?? ["tests"],
            sourceSessionId);

    private static string MediaType(string contentType) =>
        contentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];

    private static string DecodeUtf8WithoutBom(byte[] bytes)
    {
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble),
            "Exported UTF-8 must not include a byte-order mark.");
        return StrictUtf8.GetString(bytes);
    }

    private static void AssertSafeStandaloneFileName(string fileName, string extension)
    {
        Assert.False(string.IsNullOrWhiteSpace(fileName));
        Assert.Equal(fileName, Path.GetFileName(fileName));
        Assert.EndsWith(extension, fileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileName, character =>
            Path.GetInvalidFileNameChars().Contains(character));
        Assert.False(string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName)));
    }

    private static int CountPdfPages(string pdf) =>
        Regex.Matches(
            pdf,
            @"/Type\s*/Page(?!s)\b",
            RegexOptions.CultureInvariant).Count;

    private static IReadOnlyDictionary<string, byte[]> ReadZip(NoteExportArtifact artifact)
    {
        using MemoryStream stream = new(artifact.Content, writable: false);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            Assert.True(result.TryAdd(entry.FullName, ReadEntry(entry)),
                $"The archive contains duplicate entry path '{entry.FullName}'.");
        }

        return result;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using Stream entryStream = entry.Open();
        using MemoryStream copy = new();
        entryStream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void AssertSafeArchivePath(string path)
    {
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.False(path.StartsWith("/", StringComparison.Ordinal));
        Assert.DoesNotContain('\\', path);
        Assert.DoesNotContain(':', path);

        string[] segments = path.Split('/');
        Assert.All(segments, segment =>
        {
            Assert.False(string.IsNullOrWhiteSpace(segment));
            Assert.NotEqual(".", segment);
            Assert.NotEqual("..", segment);
            Assert.DoesNotContain(segment, character => "<>:\"|?*".Contains(character));
            Assert.False(segment.EndsWith(' ') || segment.EndsWith('.'));
        });

        string stem = Path.GetFileNameWithoutExtension(segments[^1]);
        Assert.False(IsWindowsReservedName(stem),
            $"Archive entry '{path}' uses a Windows-reserved filename.");
    }

    private static bool IsWindowsReservedName(string stem)
    {
        string candidate = stem.Split('.')[0];
        return Regex.IsMatch(
            candidate,
            @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasFolder(string path, string folder) =>
        path.Split('/').SkipLast(1).Contains(folder, StringComparer.Ordinal);

    private static (string Frontmatter, string Body) SplitFrontmatter(string markdown)
    {
        Match frontmatter = Regex.Match(
            markdown,
            @"\A---\r?\n(?<metadata>.*?)\r?\n---\r?\n\r?\n",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(frontmatter.Success,
            "Obsidian markdown must open and close frontmatter before the body.");

        return (
            frontmatter.Groups["metadata"].Value,
            markdown[frontmatter.Length..]);
    }

    private static void AssertMetadata(string frontmatter, string keyFragment, string value)
    {
        Assert.Matches(
            new Regex(
                $"(?ims)^\\s*[^\\r\\n:]*{Regex.Escape(keyFragment)}[^\\r\\n:]*\\s*:",
                RegexOptions.CultureInvariant),
            frontmatter);
        Assert.Contains(value, frontmatter, StringComparison.Ordinal);
    }

    private static bool HasNumericSuffix(string stem, int expected) =>
        Regex.IsMatch(
            stem,
            $@"(?:^|\D){expected}\D*$",
            RegexOptions.CultureInvariant);
}

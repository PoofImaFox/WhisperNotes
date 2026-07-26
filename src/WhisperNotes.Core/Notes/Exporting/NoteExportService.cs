using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using WhisperNotes.Core.Notes.Documents;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace WhisperNotes.Core.Notes.Exporting;

/// <summary>
/// Produces self-contained, portable exports of authored notes.
/// </summary>
/// <remarks>
/// The service is stateless. All formats are built in memory and the supplied immutable documents
/// are only read, making an instance safe to reuse.
/// </remarks>
public sealed class NoteExportService : INoteExportService
{
    private const string UnfiledProject = "_unfiled";
    private const int MaximumNameLength = 100;
    private const double PageMargin = 54;
    private const double FooterHeight = 28;

    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly MarkdownPipeline HtmlPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly Regex HtmlDestinationAttribute = new(
        @"\b(?<attribute>href|src)=""(?<destination>[^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public NoteExportArtifact Export(NoteDocument document, NoteExportFormat format)
    {
        ValidateDocument(document);

        return format switch
        {
            NoteExportFormat.Markdown => new NoteExportArtifact(
                BuildFileName(document.Title, ".md"),
                "text/markdown; charset=utf-8",
                Encode(document.Content)),
            NoteExportFormat.Html => ExportHtml(document),
            NoteExportFormat.Pdf => ExportPdf(document),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported note export format."),
        };
    }

    /// <inheritdoc />
    public NoteExportArtifact ExportObsidian(IReadOnlyList<NoteDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var prepared = new List<VaultDocument>(documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index]
                ?? throw new ArgumentException($"Document at index {index} is null.", nameof(documents));
            ValidateDocument(document);

            prepared.Add(new VaultDocument(
                document,
                SanitizePathSegment(document.Project, UnfiledProject),
                SanitizePathSegment(document.Title, "Untitled")));
        }

        // Sorting before assigning suffixes makes collisions stable even when a repository returns
        // the same documents in a different order.
        prepared.Sort(static (left, right) =>
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.ProjectFolder, right.ProjectFolder);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.NoteName, right.NoteName);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Document.Id, right.Document.Id);
        });

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in prepared)
            {
                var path = UniqueVaultPath(item.ProjectFolder, item.NoteName, paths);
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                // ZIP's timestamp range starts in 1980. A fixed value also makes identical vault
                // inputs reproducible rather than leaking the export time into the archive.
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

                using var entryStream = entry.Open();
                var content = Encode(BuildVaultNote(item.Document));
                entryStream.Write(content);
            }
        }

        return new NoteExportArtifact(
            "notes-obsidian-vault.zip",
            "application/zip",
            stream.ToArray());
    }

    private static NoteExportArtifact ExportHtml(NoteDocument document)
    {
        var title = EncodeHtmlText(document.Title);
        var project = EncodeHtmlText(EffectiveProject(document.Project));
        var created = WebUtility.HtmlEncode(FormatTimestamp(document.CreatedUtc));
        var updated = WebUtility.HtmlEncode(FormatTimestamp(document.UpdatedUtc));
        var tags = EncodeHtmlText(string.Join(", ", document.Tags));
        var markdown = SanitizeRenderedHtml(Markdown.ToHtml(document.Content, HtmlPipeline));

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data: https: http:; object-src 'none'; base-uri 'none'; form-action 'none'">
              <meta name="note-created" content="{{created}}">
              <meta name="note-updated" content="{{updated}}">
              <title>{{title}}</title>
              <style>
                :root { color-scheme: light dark; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; }
                body { max-width: 48rem; margin: 0 auto; padding: 2.5rem 1.25rem 4rem; line-height: 1.65; }
                header { margin-bottom: 2rem; padding-bottom: 1rem; border-bottom: 1px solid #8886; }
                h1 { line-height: 1.2; margin: 0 0 .75rem; }
                dl { display: grid; grid-template-columns: max-content 1fr; gap: .2rem 1rem; margin: 0; color: #777; }
                dt { font-weight: 600; } dd { margin: 0; overflow-wrap: anywhere; }
                main { overflow-wrap: anywhere; } pre { overflow-x: auto; padding: 1rem; background: #8882; }
                code { font-family: ui-monospace, "Cascadia Mono", monospace; }
                blockquote { margin-left: 0; padding-left: 1rem; border-left: .25rem solid #8887; color: #777; }
                img { max-width: 100%; height: auto; } table { border-collapse: collapse; }
                th, td { border: 1px solid #8886; padding: .35rem .6rem; text-align: left; }
              </style>
            </head>
            <body>
              <header>
                <h1>{{title}}</h1>
                <dl>
                  <dt>Project</dt><dd>{{project}}</dd>
                  <dt>Created</dt><dd>{{created}}</dd>
                  <dt>Updated</dt><dd>{{updated}}</dd>
                  <dt>Tags</dt><dd>{{tags}}</dd>
                </dl>
              </header>
              <main>
            {{markdown}}
              </main>
            </body>
            </html>
            """;

        return new NoteExportArtifact(
            BuildFileName(document.Title, ".html"),
            "text/html; charset=utf-8",
            Encode(html));
    }

    private static NoteExportArtifact ExportPdf(NoteDocument document)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = SanitizePdfText(document.Title);
        pdf.Info.Subject = "Exported note";
        pdf.Info.Creator = "WhisperNotes";
        pdf.Info.CreationDate = document.CreatedUtc.UtcDateTime;
        pdf.Info.ModificationDate = document.UpdatedUtc.UtcDateTime;
        pdf.Info.Keywords = SanitizePdfText(string.Join(", ", document.Tags));

        var unicodeOptions = new XPdfFontOptions(PdfFontEncoding.Unicode);
        var titleFont = new XFont("Segoe UI", 20, XFontStyleEx.Bold, unicodeOptions);
        var metadataFont = new XFont("Segoe UI", 9, XFontStyleEx.Regular, unicodeOptions);
        var bodyFont = new XFont("Segoe UI", 10.5, XFontStyleEx.Regular, unicodeOptions);
        var footerFont = new XFont("Segoe UI", 8, XFontStyleEx.Regular, unicodeOptions);

        var writer = new PdfPageWriter(pdf, PageMargin, FooterHeight);
        writer.WriteWrapped(SanitizePdfText(document.Title), titleFont, XBrushes.Black, lineSpacing: 8);
        writer.WriteWrapped(
            SanitizePdfText(
                $"Project: {EffectiveProject(document.Project)}   Created: {FormatTimestamp(document.CreatedUtc)}"),
            metadataFont,
            XBrushes.DimGray,
            lineSpacing: 3);
        writer.WriteWrapped(
            SanitizePdfText(
                $"Updated: {FormatTimestamp(document.UpdatedUtc)}   Tags: {string.Join(", ", document.Tags)}"),
            metadataFont,
            XBrushes.DimGray,
            lineSpacing: 9);

        foreach (var line in SplitLines(document.Content))
        {
            writer.WriteWrapped(SanitizePdfText(line), bodyFont, XBrushes.Black, lineSpacing: 2);
        }

        writer.AddPageNumbers(footerFont);

        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return new NoteExportArtifact(
            BuildFileName(document.Title, ".pdf"),
            "application/pdf",
            stream.ToArray());
    }

    private static string BuildVaultNote(NoteDocument document)
    {
        var builder = new StringBuilder(document.Content.Length + 256);
        builder.AppendLine("---");
        builder.Append("title: ").AppendLine(Yaml.Scalar(document.Title));
        builder.Append("project: ").AppendLine(Yaml.Scalar(EffectiveProject(document.Project)));
        builder.Append("created: ").AppendLine(Yaml.Scalar(document.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        builder.Append("updated: ").AppendLine(Yaml.Scalar(document.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        builder.AppendLine("tags:");

        foreach (var tag in document.Tags)
        {
            builder.Append("  - ").AppendLine(Yaml.Scalar(tag));
        }

        if (!string.IsNullOrWhiteSpace(document.SourceSessionId))
        {
            builder.Append("source_session_id: ").AppendLine(Yaml.Scalar(document.SourceSessionId.Trim()));
        }

        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(document.Content);
        return builder.ToString();
    }

    private static string UniqueVaultPath(string folder, string noteName, ISet<string> paths)
    {
        var stem = noteName;
        for (var suffix = 1; ; suffix++)
        {
            var fileName = suffix == 1
                ? $"{stem}.md"
                : $"{stem}-{suffix.ToString(CultureInfo.InvariantCulture)}.md";
            var path = $"{folder}/{fileName}";
            if (paths.Add(path))
            {
                return path;
            }
        }
    }

    private static string BuildFileName(string title, string extension) =>
        SanitizePathSegment(title, "Untitled") + extension;

    private static string SanitizePathSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasReplacement = false;
        foreach (var character in value.Trim())
        {
            var invalid = char.IsControl(character)
                || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';
            if (invalid)
            {
                if (!previousWasReplacement)
                {
                    builder.Append('_');
                    previousWasReplacement = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasReplacement = false;
        }

        var result = builder.ToString().Trim().Trim('.');
        if (result.Length == 0)
        {
            return fallback;
        }

        var firstNamePart = result.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(firstNamePart))
        {
            result = "_" + result;
        }

        if (result.Length > MaximumNameLength)
        {
            result = result[..MaximumNameLength].TrimEnd(' ', '.');
            if (result.Length > 0 && char.IsHighSurrogate(result[^1]))
            {
                result = result[..^1];
            }
        }

        return result.Length == 0 ? fallback : result;
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }

        if (content.Length == 0 || content.EndsWith('\n') || content.EndsWith('\r'))
        {
            yield return string.Empty;
        }
    }

    private static string SanitizePdfText(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\t')
            {
                builder.Append("    ");
            }
            else if (char.IsControl(character))
            {
                builder.Append(' ');
            }
            else if (char.IsHighSurrogate(character)
                     && (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])))
            {
                builder.Append('\uFFFD');
            }
            else if (char.IsLowSurrogate(character)
                     && (index == 0 || !char.IsHighSurrogate(text[index - 1])))
            {
                builder.Append('\uFFFD');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string EffectiveProject(string? project) =>
        string.IsNullOrWhiteSpace(project) ? UnfiledProject : project.Trim();

    private static string EncodeHtmlText(string value) =>
        WebUtility.HtmlEncode(SanitizeScalarText(value));

    /// <summary>
    /// Markdig owns the markup shape because raw HTML is disabled. This final allow-list keeps
    /// Markdown link destinations such as javascript: and data: inert as well.
    /// </summary>
    private static string SanitizeRenderedHtml(string html) =>
        HtmlDestinationAttribute.Replace(html, static match =>
        {
            var destination = WebUtility.HtmlDecode(match.Groups["destination"].Value);
            return IsSafeHtmlDestination(destination)
                ? match.Value
                : $"{match.Groups["attribute"].Value}=\"#\"";
        });

    private static bool IsSafeHtmlDestination(string destination)
    {
        destination = destination.Trim();
        if (destination.Length == 0)
        {
            return true;
        }

        var colon = destination.IndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        var schemeBuilder = new StringBuilder(colon);
        for (var index = 0; index < colon; index++)
        {
            var character = destination[index];
            if (!char.IsWhiteSpace(character) && !char.IsControl(character))
            {
                schemeBuilder.Append(character);
            }
        }

        var scheme = schemeBuilder.ToString();
        return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeScalarText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    private static byte[] Encode(string content) => Utf8WithoutBom.GetBytes(content);

    private static void ValidateDocument(NoteDocument? document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Title);
        ArgumentNullException.ThrowIfNull(document.Content);
        ArgumentNullException.ThrowIfNull(document.Tags);

        for (var index = 0; index < document.Tags.Count; index++)
        {
            if (document.Tags[index] is null)
            {
                throw new ArgumentException($"Tag at index {index} is null.", nameof(document));
            }
        }
    }

    private sealed record VaultDocument(NoteDocument Document, string ProjectFolder, string NoteName);

    /// <summary>Small page-aware text writer used instead of relying on a platform text formatter.</summary>
    private sealed class PdfPageWriter(PdfDocument document, double margin, double footerHeight)
    {
        private PdfPage? _page;
        private XGraphics? _graphics;
        private double _cursorY;

        public void WriteWrapped(string text, XFont font, XBrush brush, double lineSpacing)
        {
            EnsurePage();
            var availableWidth = _page!.Width.Point - (margin * 2);
            var lineHeight = font.GetHeight() + lineSpacing;

            foreach (var line in Wrap(text, font, availableWidth))
            {
                EnsureSpace(lineHeight);
                _graphics!.DrawString(
                    line.Length == 0 ? " " : line,
                    font,
                    brush,
                    new XPoint(margin, _cursorY + font.GetHeight()),
                    XStringFormats.Default);
                _cursorY += lineHeight;
            }
        }

        public void AddPageNumbers(XFont font)
        {
            _graphics?.Dispose();
            _graphics = null;

            for (var index = 0; index < document.PageCount; index++)
            {
                var page = document.Pages[index];
                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var y = page.Height.Point - margin + 10;
                graphics.DrawLine(XPens.LightGray, margin, y - 12, page.Width.Point - margin, y - 12);
                graphics.DrawString(
                    $"Page {index + 1} of {document.PageCount}",
                    font,
                    XBrushes.DimGray,
                    new XRect(margin, y - 4, page.Width.Point - (margin * 2), font.GetHeight() + 4),
                    XStringFormats.TopCenter);
            }
        }

        private IEnumerable<string> Wrap(string text, XFont font, double availableWidth)
        {
            if (text.Length == 0)
            {
                yield return string.Empty;
                yield break;
            }

            var remaining = text;
            while (remaining.Length > 0)
            {
                if (_graphics!.MeasureString(remaining, font).Width <= availableWidth)
                {
                    yield return remaining;
                    yield break;
                }

                var fit = FindFittingLength(remaining, font, availableWidth);
                var breakAt = LastWhitespaceBefore(remaining, fit);
                if (breakAt <= 0)
                {
                    breakAt = fit;
                }

                yield return remaining[..breakAt].TrimEnd();
                var next = breakAt;
                while (next < remaining.Length && char.IsWhiteSpace(remaining[next]))
                {
                    next++;
                }

                remaining = remaining[next..];
            }
        }

        private int FindFittingLength(string text, XFont font, double availableWidth)
        {
            var elementStarts = StringInfo.ParseCombiningCharacters(text);
            var low = 1;
            var high = elementStarts.Length;
            while (low < high)
            {
                var middle = low + ((high - low + 1) / 2);
                var end = middle == elementStarts.Length ? text.Length : elementStarts[middle];
                if (_graphics!.MeasureString(text[..end], font).Width <= availableWidth)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return low == elementStarts.Length ? text.Length : elementStarts[low];
        }

        private static int LastWhitespaceBefore(string text, int maximum)
        {
            for (var index = Math.Min(maximum, text.Length - 1); index > 0; index--)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        private void EnsureSpace(double requiredHeight)
        {
            EnsurePage();
            var contentBottom = _page!.Height.Point - margin - footerHeight;
            if (_cursorY + requiredHeight <= contentBottom)
            {
                return;
            }

            StartPage();
        }

        private void EnsurePage()
        {
            if (_page is null)
            {
                StartPage();
            }
        }

        private void StartPage()
        {
            _graphics?.Dispose();
            _page = document.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(_page);
            _cursorY = margin;
        }
    }
}

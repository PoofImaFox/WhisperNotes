using WhisperNotes.Core.Notes.Documents;

namespace WhisperNotes.Core.Notes.Exporting;

/// <summary>The portable formats supported by <see cref="INoteExportService"/>.</summary>
public enum NoteExportFormat
{
    Markdown,
    Html,
    Pdf,
}

/// <summary>
/// A complete in-memory export and the metadata a caller needs to offer it as a download.
/// </summary>
/// <param name="SuggestedFileName">A filesystem-safe file name including its extension.</param>
/// <param name="ContentType">The artifact's Internet media type.</param>
/// <param name="Content">The complete artifact bytes.</param>
public sealed record NoteExportArtifact(
    string SuggestedFileName,
    string ContentType,
    byte[] Content);

/// <summary>Exports standalone note documents without changing their stored representation.</summary>
public interface INoteExportService
{
    /// <summary>Exports one document in <paramref name="format"/>.</summary>
    NoteExportArtifact Export(NoteDocument document, NoteExportFormat format);

    /// <summary>
    /// Packages documents as a plain Markdown vault that can be opened directly by Obsidian.
    /// </summary>
    NoteExportArtifact ExportObsidian(IReadOnlyList<NoteDocument> documents);
}

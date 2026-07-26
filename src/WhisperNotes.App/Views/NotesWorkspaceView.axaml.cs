using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WhisperNotes.App.ViewModels;
using WhisperNotes.Core.Notes.Exporting;

namespace WhisperNotes.App.Views;

/// <summary>
/// The Notes page: library, editor and the assistant/history rail. The shell hosts exactly this
/// control, bound to a <see cref="ViewModels.NotesWorkspaceViewModel"/>.
/// </summary>
public partial class NotesWorkspaceView : UserControl
{
    public NotesWorkspaceView()
    {
        InitializeComponent();
    }

    private void OnExportMarkdownClick(object? sender, RoutedEventArgs e) =>
        _ = ExportNoteAsync(NoteExportFormat.Markdown);

    private void OnExportHtmlClick(object? sender, RoutedEventArgs e) =>
        _ = ExportNoteAsync(NoteExportFormat.Html);

    private void OnExportPdfClick(object? sender, RoutedEventArgs e) =>
        _ = ExportNoteAsync(NoteExportFormat.Pdf);

    private void OnExportObsidianClick(object? sender, RoutedEventArgs e) =>
        _ = ExportObsidianAsync();

    private async Task ExportObsidianAsync()
    {
        if (DataContext is not NotesWorkspaceViewModel viewModel || !viewModel.TryBeginExport())
        {
            return;
        }

        try
        {
            var artifact = await viewModel.PrepareObsidianExportAsync().ConfigureAwait(true);
            if (artifact is not null)
            {
                await SaveArtifactAsync(viewModel, artifact, ExportFileKind.Obsidian, isLibrary: true)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // The routed handler deliberately starts this guarded Task instead of being async void,
            // so picker failures cannot escape to Avalonia's dispatcher and tear down the app.
            viewModel.ReportExportSaveFailure(ex, isLibrary: true);
        }
        finally
        {
            viewModel.FinishExport();
        }
    }

    private async Task ExportNoteAsync(NoteExportFormat format)
    {
        if (DataContext is not NotesWorkspaceViewModel viewModel || !viewModel.TryBeginExport())
        {
            return;
        }

        try
        {
            var artifact = await viewModel.PrepareNoteExportAsync(format).ConfigureAwait(true);
            if (artifact is not null)
            {
                await SaveArtifactAsync(viewModel, artifact, ToFileKind(format), isLibrary: false)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportExportSaveFailure(ex, isLibrary: false);
        }
        finally
        {
            viewModel.FinishExport();
        }
    }

    private async Task SaveArtifactAsync(
        NotesWorkspaceViewModel viewModel,
        NoteExportArtifact artifact,
        ExportFileKind kind,
        bool isLibrary)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            viewModel.ReportExportSaveFailure(
                new InvalidOperationException("The system save picker is unavailable."),
                isLibrary);
            return;
        }

        IStorageFile? destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = isLibrary ? "Save Obsidian library export" : "Save note export",
            SuggestedFileName = artifact.SuggestedFileName,
            DefaultExtension = GetExtension(kind),
            FileTypeChoices = [GetFileType(kind)],
            ShowOverwritePrompt = true
        });

        // Closing the picker is ordinary cancellation, not an error or a notification-worthy event.
        if (destination is null)
        {
            return;
        }

        await using Stream output = await destination.OpenWriteAsync();
        if (output.CanSeek)
        {
            output.SetLength(0);
        }

        await output.WriteAsync(artifact.Content).ConfigureAwait(true);
        await output.FlushAsync().ConfigureAwait(true);

        viewModel.ReportExportSaved(destination.Name, isLibrary);
    }

    private static ExportFileKind ToFileKind(NoteExportFormat format) => format switch
    {
        NoteExportFormat.Markdown => ExportFileKind.Markdown,
        NoteExportFormat.Html => ExportFileKind.Html,
        NoteExportFormat.Pdf => ExportFileKind.Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static string GetExtension(ExportFileKind kind) => kind switch
    {
        ExportFileKind.Markdown => "md",
        ExportFileKind.Html => "html",
        ExportFileKind.Pdf => "pdf",
        ExportFileKind.Obsidian => "zip",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static FilePickerFileType GetFileType(ExportFileKind kind) => kind switch
    {
        ExportFileKind.Markdown => new FilePickerFileType("Markdown document")
        {
            Patterns = ["*.md"],
            MimeTypes = ["text/markdown"]
        },
        ExportFileKind.Html => new FilePickerFileType("HTML document")
        {
            Patterns = ["*.html", "*.htm"],
            MimeTypes = ["text/html"]
        },
        ExportFileKind.Pdf => new FilePickerFileType("PDF document")
        {
            Patterns = ["*.pdf"],
            MimeTypes = ["application/pdf"]
        },
        ExportFileKind.Obsidian => new FilePickerFileType("ZIP archive")
        {
            Patterns = ["*.zip"],
            MimeTypes = ["application/zip"]
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private enum ExportFileKind
    {
        Markdown,
        Html,
        Pdf,
        Obsidian
    }
}

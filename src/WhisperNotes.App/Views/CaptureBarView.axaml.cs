using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WhisperNotes.App.ViewModels;

namespace WhisperNotes.App.Views;

public partial class CaptureBarView : UserControl
{
    public CaptureBarView() => InitializeComponent();

    private async void OnImportVideoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageFile> selected = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a recording to transcribe",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Video files")
                    {
                        Patterns = ["*.mp4", "*.m4v", "*.mov", "*.mkv", "*.avi", "*.webm", "*.wmv"]
                    },
                    FilePickerFileTypes.All
                ]
            });

            if (selected.Count > 0 && selected[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                await viewModel.ImportVideoAsync(path).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportVideoPickerFailure(ex);
        }
    }
}

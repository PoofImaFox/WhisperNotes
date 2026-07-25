using NoteScribe.App.Composition;
using NoteScribe.App.ViewModels;

namespace NoteScribe.App.DesignData;

/// <summary>Entry points for <c>d:DataContext</c> so every view previews with realistic content.</summary>
public static class DesignTime
{
    public static MainWindowViewModel Main { get; } = Build();

    public static CaptureViewModel Capture => Main.Capture;

    public static NotesBrowserViewModel Browser => Main.Browser;

    public static SessionDocumentViewModel Document => Main.Document;

    private static MainWindowViewModel Build()
    {
        var vm = new MainWindowViewModel(AppServices.CreateDesignTime());
        _ = vm.InitializeAsync();
        return vm;
    }
}

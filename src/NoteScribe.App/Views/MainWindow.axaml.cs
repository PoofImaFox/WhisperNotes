using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NoteScribe.App.ViewModels;

namespace NoteScribe.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Page shortcuts, belt and braces. Window.KeyBindings alone is not enough for two
        // independent reasons:
        //
        //  1. Gesture parsing. "Ctrl+1" LOOKS right and compiles, but Avalonia parses the key
        //     token with Enum.TryParse<Key>, and .NET's Enum.TryParse happily accepts a numeric
        //     string as an underlying VALUE. "1" therefore becomes (Key)1 == Key.Cancel and "2"
        //     becomes (Key)2 == Key.Back — so the bindings were listening for Ctrl+Break and
        //     Ctrl+Backspace. No exception, no warning, just a shortcut that never fires. The
        //     digit row is Key.D1/Key.D2 (35/36), hence the Ctrl+D1/Ctrl+D2 gestures in the XAML.
        //
        //  2. Focus. KeyBindings are evaluated on the BUBBLING KeyDown route, so the focused
        //     control gets first refusal. With the caret in a TextBox — the session title, the
        //     note composer, the AI instruction box — the shortcut is at the mercy of whatever
        //     that control does with the key. Handling it on the TUNNELLING route runs this
        //     handler on the way down from the window to the focused element, before anything can
        //     swallow it, so the shortcut works from anywhere in the shell.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Exact match: Ctrl+Shift+1 and friends belong to whatever else may want them.
        if (e.KeyModifiers != KeyModifiers.Control || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        ShellPage page;
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1:
                page = ShellPage.Meeting;
                break;
            case Key.D2 or Key.NumPad2:
                page = ShellPage.Notes;
                break;
            default:
                return;
        }

        if (viewModel.GoToPageCommand.CanExecute(page))
        {
            viewModel.GoToPageCommand.Execute(page);
        }

        // Marked handled either way: Ctrl+2 would otherwise reach a focused TextBox as a plain
        // Ctrl+digit and, worse, the old mis-parsed binding meant Ctrl+Backspace semantics.
        e.Handled = true;
    }
}

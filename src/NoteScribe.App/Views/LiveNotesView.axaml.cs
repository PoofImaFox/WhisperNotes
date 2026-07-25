using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NoteScribe.App.ViewModels;

namespace NoteScribe.App.Views;

/// <summary>
/// View concerns only: log-view scroll behaviour (follow the tail until the user scrolls away)
/// and routing the in-place edit gestures to the row's commands.
/// </summary>
public partial class LiveNotesView : UserControl
{
    private const double StickToBottomThreshold = 32;

    private SessionDocumentViewModel? _document;
    private bool _programmaticScroll;

    public LiveNotesView()
    {
        InitializeComponent();
        NotesScroll.ScrollChanged += OnScrollChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_document is not null)
        {
            _document.EntryAppended -= OnEntryAppended;
            _document.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        _document = DataContext as SessionDocumentViewModel;

        if (_document is not null)
        {
            _document.EntryAppended += OnEntryAppended;
            _document.ScrollToEndRequested += OnScrollToEndRequested;
        }
    }

    private void OnEntryAppended(object? sender, EventArgs e)
    {
        if (_document?.AutoScroll == true)
        {
            ScrollToEnd();
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        // The new row has not been measured yet when the collection change fires.
        Dispatcher.UIThread.Post(
            () =>
            {
                _programmaticScroll = true;
                NotesScroll.ScrollToEnd();
                _programmaticScroll = false;
            },
            DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Only a real offset change is a user gesture; growing content moves the extent, not the offset.
        if (_programmaticScroll || _document is null || Math.Abs(e.OffsetDelta.Y) < double.Epsilon)
        {
            return;
        }

        var distanceFromBottom = NotesScroll.Extent.Height - NotesScroll.Viewport.Height - NotesScroll.Offset.Y;
        _document.AutoScroll = distanceFromBottom <= StickToBottomThreshold;
    }

    private void OnEntryTextDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: NoteEntryViewModel entry } && entry.BeginEditCommand.CanExecute(null))
        {
            entry.BeginEditCommand.Execute(null);
        }
    }

    private void OnEntryEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        // Clicking away commits rather than silently discarding a correction.
        if (sender is Control { DataContext: NoteEntryViewModel { IsEditing: true } entry })
        {
            entry.CommitEditCommand.Execute(null);
        }
    }
}

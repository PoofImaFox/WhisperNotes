using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WhisperNotes.App.ViewModels;

namespace WhisperNotes.App.Views;

/// <summary>
/// View concerns only: log-view scroll behaviour (follow the tail until the user scrolls away)
/// and routing the in-place edit gestures to the row's commands.
/// </summary>
public partial class LiveNotesView : UserControl
{
    /// <summary>How close to the bottom still counts as "watching the tail".</summary>
    private const double StickToBottomThreshold = 32;

    /// <summary>Nominal row line height, used for wheel and arrow-key steps.</summary>
    private const double LineHeight = 22;

    private const int WheelLines = 3;

    /// <summary>
    /// A single pass is never enough: the row that triggered the append has not been measured
    /// when the collection change fires, and realising the tail containers moves the extent again.
    /// </summary>
    private const int PinPasses = 3;

    private SessionDocumentViewModel? _document;
    private IScrollable? _scroll;
    private PinTarget _pinTarget;
    private int _pinPassesLeft;
    private bool _pinQueued;
    private int _programmaticDepth;
    private double _expectedOffsetY = double.NaN;
    private bool _hadInterim;

    public LiveNotesView()
    {
        InitializeComponent();

        // ScrollChanged bubbles out of the list's own templated scroll viewer.
        NotesLog.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);

        // Tunnel, so the log reacts before the list's own key/wheel handling, but never while
        // the caret is inside an entry being corrected.
        NotesLog.AddHandler(KeyDownEvent, OnLogKeyDown, RoutingStrategies.Tunnel);
        NotesLog.AddHandler(PointerWheelChangedEvent, OnLogPointerWheel, RoutingStrategies.Tunnel);
        NotesLog.AddHandler(PointerPressedEvent, OnLogPointerPressed, RoutingStrategies.Tunnel);

        DataContextChanged += OnDataContextChanged;
    }

    private enum PinTarget
    {
        None,
        Bottom,
        Top
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_document is not null)
        {
            _document.EntryAppended -= OnEntryAppended;
            _document.ScrollToEndRequested -= OnScrollToEndRequested;
            _document.ScrollToTopRequested -= OnScrollToTopRequested;
            _document.PropertyChanged -= OnDocumentPropertyChanged;
        }

        _document = DataContext as SessionDocumentViewModel;

        if (_document is not null)
        {
            _document.EntryAppended += OnEntryAppended;
            _document.ScrollToEndRequested += OnScrollToEndRequested;
            _document.ScrollToTopRequested += OnScrollToTopRequested;
            _document.PropertyChanged += OnDocumentPropertyChanged;
            _hadInterim = _document.HasInterim;
        }
    }

    private void OnEntryAppended(object? sender, EventArgs e)
    {
        if (_document?.AutoScroll == true)
        {
            RequestPin(PinTarget.Bottom);
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => RequestPin(PinTarget.Bottom);

    private void OnScrollToTopRequested(object? sender, EventArgs e) => RequestPin(PinTarget.Top);

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_document is null || e.PropertyName is not (nameof(SessionDocumentViewModel.InterimText)
            or nameof(SessionDocumentViewModel.HasInterim)))
        {
            return;
        }

        // The interim strip keeps a reserved height so it can no longer resize the log's
        // viewport, but re-pinning on the flip costs nothing and covers any residual shift.
        var hasInterim = _document.HasInterim;
        if (hasInterim != _hadInterim)
        {
            _hadInterim = hasInterim;
            if (_document.AutoScroll)
            {
                RequestPin(PinTarget.Bottom);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Scroll pinning
    // ---------------------------------------------------------------------------------------

    private IScrollable? ResolveScroll() =>
        _scroll ??= NotesLog.Scroll ?? NotesLog.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void RequestPin(PinTarget target)
    {
        _pinTarget = target;
        _pinPassesLeft = PinPasses;
        QueuePin(DispatcherPriority.Loaded);
    }

    private void QueuePin(DispatcherPriority priority)
    {
        if (_pinQueued || _pinTarget == PinTarget.None)
        {
            return;
        }

        _pinQueued = true;
        Dispatcher.UIThread.Post(RunPin, priority);
    }

    private void RunPin()
    {
        _pinQueued = false;

        if (_pinTarget == PinTarget.None)
        {
            return;
        }

        var scroll = ResolveScroll();
        if (scroll is null)
        {
            // Template not applied yet — try again once the list exists.
            if (--_pinPassesLeft > 0)
            {
                QueuePin(DispatcherPriority.Background);
            }
            else
            {
                _pinTarget = PinTarget.None;
            }

            return;
        }

        double target;

        if (_pinTarget == PinTarget.Top)
        {
            target = 0;
        }
        else
        {
            // Measure now: without this the extent is still the pre-append value and the scroll
            // lands short of the real bottom.
            NotesLog.UpdateLayout();

            var last = (_document?.Entries.Count ?? 0) - 1;
            if (last >= 0)
            {
                // Virtualisation makes Extent an estimate; realising the final container first is
                // what makes the offset below land on the true bottom.
                BeginProgrammatic();
                try
                {
                    NotesLog.ScrollIntoView(last);
                }
                finally
                {
                    EndProgrammatic();
                }

                NotesLog.UpdateLayout();
            }

            // Never below where ScrollIntoView already put us: with variable-height rows the
            // extent is an average-based estimate and can briefly read short.
            target = Math.Max(scroll.Offset.Y, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        }

        if (Math.Abs(scroll.Offset.Y - target) > 0.5)
        {
            BeginProgrammatic();
            try
            {
                _expectedOffsetY = target;
                scroll.Offset = new Vector(scroll.Offset.X, target);
            }
            finally
            {
                EndProgrammatic();
            }
        }

        if (--_pinPassesLeft > 0)
        {
            QueuePin(DispatcherPriority.Background);
        }
        else
        {
            _pinTarget = PinTarget.None;
        }
    }

    private void BeginProgrammatic() => _programmaticDepth++;

    private void EndProgrammatic() =>
        // ScrollChanged is raised from the layout pass that follows, not from the assignment, so
        // the latch has to outlive this call — release it below layout/render priority.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_programmaticDepth > 0)
                {
                    _programmaticDepth--;
                }
            },
            DispatcherPriority.Background);

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scroll = ResolveScroll();
        if (_document is null || scroll is null)
        {
            return;
        }

        // Rows contain text boxes, which scroll themselves; only the log's own scroll viewer
        // says anything about whether the user is still watching the tail.
        if (!ReferenceEquals(e.Source, scroll))
        {
            return;
        }

        var distanceFromBottom = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;

        // Two independent guards, because neither is sufficient on its own: the latch covers the
        // window in which we drive the offset, and the recorded offset identifies our own move
        // even if the event only reaches us after the latch has been released.
        var ours = _programmaticDepth > 0
                   || (!double.IsNaN(_expectedOffsetY) && Math.Abs(scroll.Offset.Y - _expectedOffsetY) <= 1.0);

        if (Math.Abs(e.OffsetDelta.Y) > double.Epsilon)
        {
            _expectedOffsetY = double.NaN;

            if (!ours)
            {
                // Only a real, user-driven offset change decides whether we still follow the tail.
                _document.AutoScroll = distanceFromBottom <= StickToBottomThreshold;
            }
        }

        // Content grew or the viewport shrank while pinned: the offset did not move, so this is
        // the only signal that we have silently drifted off the bottom.
        var geometryMoved = Math.Abs(e.ExtentDelta.Y) > double.Epsilon
                            || Math.Abs(e.ViewportDelta.Y) > double.Epsilon;

        if (geometryMoved && _document.AutoScroll && distanceFromBottom > 0.5)
        {
            RequestPin(PinTarget.Bottom);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Keyboard and wheel
    // ---------------------------------------------------------------------------------------

    private void OnLogPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Item containers are deliberately not focusable (no selection chrome), so the list
        // itself has to take focus for the keyboard scrolling below to have a target.
        if (IsWithinInput(e.Source))
        {
            return;
        }

        NotesLog.Focus(NavigationMethod.Pointer);
    }

    private void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsWithinInput(e.Source))
        {
            return;
        }

        var scroll = ResolveScroll();
        if (scroll is null)
        {
            return;
        }

        var page = Math.Max(LineHeight, scroll.Viewport.Height - LineHeight);
        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

        double? target = e.Key switch
        {
            Key.PageUp => scroll.Offset.Y - page,
            Key.PageDown => scroll.Offset.Y + page,
            Key.Home => 0,
            Key.End => max,
            Key.Up => scroll.Offset.Y - LineHeight,
            Key.Down => scroll.Offset.Y + LineHeight,
            _ => null
        };

        if (target is not { } y)
        {
            return;
        }

        ScrollTo(scroll, y, max);
        e.Handled = true;
    }

    private void OnLogPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        var scroll = ResolveScroll();
        if (scroll is null || e.Delta.Y == 0)
        {
            return;
        }

        var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        if (max <= 0)
        {
            return;
        }

        ScrollTo(scroll, scroll.Offset.Y - (e.Delta.Y * WheelLines * LineHeight), max);
        e.Handled = true;
    }

    /// <summary>A deliberate user move: clamp it, then let ScrollChanged re-evaluate stickiness.</summary>
    private static void ScrollTo(IScrollable scroll, double y, double max)
    {
        var clamped = Math.Clamp(y, 0, max);
        if (Math.Abs(clamped - scroll.Offset.Y) > double.Epsilon)
        {
            scroll.Offset = new Vector(scroll.Offset.X, clamped);
        }
    }

    private static bool IsWithinInput(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null;

    // ---------------------------------------------------------------------------------------
    // In-place editing
    // ---------------------------------------------------------------------------------------

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

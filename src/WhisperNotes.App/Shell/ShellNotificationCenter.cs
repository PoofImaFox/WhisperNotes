using System.Collections.ObjectModel;
using Avalonia.Threading;
using WhisperNotes.App.ViewModels;

namespace WhisperNotes.App.Shell;

/// <summary>
/// Owns the floating banner stack: what is on screen, how a repeat replaces its predecessor, and
/// the cap that stops a failing device turning the window into a wall of toasts. Every failure
/// path in the shell lands here rather than in an exception dialog.
/// </summary>
internal sealed class ShellNotificationCenter(Action onChanged)
{
    /// <summary>Four is the app contract's depth; past that the stack is noise, not signal.</summary>
    private const int MaxVisible = 4;

    /// <summary>Bound directly by the overlay, so it is created once and never replaced.</summary>
    public ObservableCollection<NotificationViewModel> Items { get; } = [];

    public bool HasAny => Items.Count > 0;

    public void Post(string title, string message, NotificationSeverity severity) =>
        Post(title, message, severity, null, null);

    /// <summary>
    /// Argument-order adapter for the pages that hand out an
    /// <c>Action&lt;NotificationSeverity, string, string&gt;</c> rather than the shell's own shape.
    /// </summary>
    public void Report(NotificationSeverity severity, string title, string message) =>
        Post(title, message, severity, null, null);

    /// <param name="actionLabel">Caption for an optional one-click follow-up, e.g. "Open folder".</param>
    /// <param name="action">Runs on the UI thread when that caption is clicked.</param>
    public void Post(
        string title,
        string message,
        NotificationSeverity severity,
        string? actionLabel,
        Action? action)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Post(title, message, severity, actionLabel, action));
            return;
        }

        var existing = Items.FirstOrDefault(n =>
            string.Equals(n.Title, title, StringComparison.Ordinal) && n.CanDismiss);

        if (existing is not null)
        {
            Items.Remove(existing);
        }

        Items.Add(new NotificationViewModel(severity, title, message, Dismiss, true, actionLabel, action));

        // Keep the banner stack short; the oldest dismissible one goes first.
        while (Items.Count > MaxVisible)
        {
            var oldest = Items.FirstOrDefault(n => n.CanDismiss);
            if (oldest is null)
            {
                break;
            }

            Items.Remove(oldest);
        }

        onChanged();
    }

    public void Dismiss(NotificationViewModel notification)
    {
        Items.Remove(notification);
        onChanged();
    }
}

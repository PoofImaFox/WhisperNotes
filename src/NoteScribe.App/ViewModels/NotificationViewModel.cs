using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NoteScribe.App.ViewModels;

public enum NotificationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A dismissible banner. Every failure path lands here rather than in an exception dialog.</summary>
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly Action<NotificationViewModel> _dismiss;
    private readonly Action? _action;

    public NotificationViewModel(
        NotificationSeverity severity,
        string title,
        string message,
        Action<NotificationViewModel> dismiss,
        bool canDismiss = true,
        string? actionLabel = null,
        Action? action = null)
    {
        Severity = severity;
        Title = title;
        Message = message;
        CanDismiss = canDismiss;
        ActionLabel = actionLabel;
        _dismiss = dismiss;
        _action = action;
    }

    public NotificationSeverity Severity { get; }

    public string Title { get; }

    public string Message { get; }

    public bool CanDismiss { get; }

    public string? ActionLabel { get; }

    public bool HasAction => _action is not null && !string.IsNullOrWhiteSpace(ActionLabel);

    public bool IsInfo => Severity == NotificationSeverity.Info;

    public bool IsWarning => Severity == NotificationSeverity.Warning;

    public bool IsError => Severity == NotificationSeverity.Error;

    [RelayCommand]
    private void Dismiss() => _dismiss(this);

    [RelayCommand]
    private void Invoke() => _action?.Invoke();
}

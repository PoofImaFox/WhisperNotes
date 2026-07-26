using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperNotes.Core.Ai;

namespace WhisperNotes.App.ViewModels;

/// <summary>One button in the assistant palette.</summary>
public sealed partial class AiActionViewModel : ObservableObject
{
    private readonly Func<AiActionViewModel, Task> _run;

    public AiActionViewModel(AiAction action, Func<AiActionViewModel, Task> run, Func<bool> canRun)
    {
        ArgumentNullException.ThrowIfNull(action);

        Action = action;
        _run = run;
        RunCommand = new AsyncRelayCommand(() => _run(this), canRun);
    }

    public AiAction Action { get; }

    public string Id => Action.Id;

    public string Name => Action.Name;

    public string Description => Action.Description;

    public string Icon => Action.Icon;

    public string Category => Action.Category;

    public bool NeedsInstruction => Action.NeedsInstruction;

    /// <summary>Tooltip body: what it does, plus what it will do with the answer.</summary>
    public string TooltipText =>
        $"{Description}\n\n{ScopeText} · {(Action.ReplacesTarget ? "replaces the target text" : "inserted below the target")}";

    public string ScopeText => Action.Scope switch
    {
        AiActionScope.Selection => "Needs a selection",
        AiActionScope.WholeDocument => "Whole note",
        _ => "Selection or whole note"
    };

    /// <summary>True while this specific action is streaming, so only its own button shows a spinner.</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    public IAsyncRelayCommand RunCommand { get; }

    public void RaiseCanExecuteChanged() => RunCommand.NotifyCanExecuteChanged();
}

/// <summary>Actions bucketed by <see cref="AiAction.Category"/> for the palette's section headers.</summary>
public sealed class AiActionGroupViewModel
{
    public AiActionGroupViewModel(string category, IReadOnlyList<AiActionViewModel> actions)
    {
        Category = category;
        Actions = actions;
    }

    public string Category { get; }

    public string CategoryText => Category.ToUpperInvariant();

    public IReadOnlyList<AiActionViewModel> Actions { get; }
}

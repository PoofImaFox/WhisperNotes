using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhisperNotes.App.Services;
using WhisperNotes.Core.Notes;

namespace WhisperNotes.App.ViewModels;

/// <summary>The left sidebar: past sessions grouped by project then day, newest first.</summary>
public sealed partial class NotesBrowserViewModel : ObservableObject
{
    private readonly INoteRepository _notes;
    private readonly Action<string, string, NotificationSeverity> _notify;
    private CancellationTokenSource? _searchDebounce;
    private CancellationTokenSource? _countFill;

    public NotesBrowserViewModel(INoteRepository notes, Action<string, string, NotificationSeverity> notify)
    {
        _notes = notes;
        _notify = notify;
    }

    /// <summary>Raised when the user picks a session to read.</summary>
    public event EventHandler<NoteSession>? SessionActivated;

    public ObservableCollection<NotesTreeNodeViewModel> Nodes { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    public partial string SummaryText { get; set; } = "";

    [ObservableProperty] public partial bool HasResults { get; set; }

    /// <summary>Bound to <c>TreeView.SelectedItem</c>, which is loosely typed.</summary>
    [ObservableProperty] public partial object? SelectedNode { get; set; }

    public string RootDirectory => _notes.RootDirectory;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var query = new NoteQuery(TextContains: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim());
            var sessions = await _notes.ListSessionsAsync(query, CancellationToken.None).ConfigureAwait(true);
            Rebuild(sessions);
        }
        catch (Exception ex)
        {
            _notify("Could not read the notes folder", ex.Message, NotificationSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenRootFolder()
    {
        try
        {
            SystemShell.OpenDirectory(_notes.RootDirectory);
        }
        catch (Exception ex)
        {
            _notify("Could not open the notes folder", ex.Message, NotificationSeverity.Warning);
        }
    }

    /// <summary>Re-reads the tree and re-selects the given session, used after a recording finishes.</summary>
    public async Task RefreshAndSelectAsync(string sessionId)
    {
        await RefreshAsync().ConfigureAwait(true);

        var node = AllSessionNodes().FirstOrDefault(n => string.Equals(n.Session.Id, sessionId, StringComparison.Ordinal));
        if (node is not null)
        {
            SelectedNode = node;
        }
    }

    private void Rebuild(IReadOnlyList<NoteSession> sessions)
    {
        _countFill?.Cancel();
        _countFill?.Dispose();
        _countFill = null;

        Nodes.Clear();

        var byProject = sessions
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Project) ? "_unfiled" : s.Project!, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Max(s => s.StartedUtc));

        foreach (var projectGroup in byProject)
        {
            var project = new ProjectNodeViewModel(projectGroup.Key, projectGroup.Count());

            var byDate = projectGroup
                .GroupBy(s => DateOnly.FromDateTime(s.StartedUtc.ToLocalTime().DateTime))
                .OrderByDescending(g => g.Key);

            foreach (var dateGroup in byDate)
            {
                var day = new DateNodeViewModel(dateGroup.Key, dateGroup.Count());

                foreach (var session in dateGroup.OrderByDescending(s => s.StartedUtc))
                {
                    day.Children.Add(new SessionNodeViewModel(session, OpenSessionFolder));
                }

                project.Children.Add(day);
            }

            Nodes.Add(project);
        }

        HasResults = sessions.Count > 0;
        SummaryText = sessions.Count switch
        {
            0 when string.IsNullOrWhiteSpace(SearchText) => "No sessions yet.",
            0 => $"Nothing matches “{SearchText.Trim()}”.",
            1 => "1 session",
            _ => $"{sessions.Count} sessions"
        };

        var fill = new CancellationTokenSource();
        _countFill = fill;
        _ = FillEntryCountsAsync(fill.Token);
    }

    /// <summary>
    /// NoteSession carries no entry count, so counts are read lazily in the background rather than
    /// blocking the list on one file read per session.
    /// </summary>
    private async Task FillEntryCountsAsync(CancellationToken cancellationToken)
    {
        foreach (var node in AllSessionNodes().Take(200))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var entries = await _notes.LoadEntriesAsync(node.Session.Id, cancellationToken).ConfigureAwait(true);
                node.EntryCountText = entries.Count == 1 ? "1 entry" : $"{entries.Count} entries";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A single unreadable session must not stop the rest of the list from counting.
                node.EntryCountText = "—";
            }
        }
    }

    private IEnumerable<SessionNodeViewModel> AllSessionNodes() =>
        Nodes.SelectMany(p => p.Children).SelectMany(d => d.Children).OfType<SessionNodeViewModel>();

    private void OpenSessionFolder(NoteSession session)
    {
        try
        {
            SystemShell.OpenDirectory(_notes.GetSessionDirectory(session.Id));
        }
        catch (Exception ex)
        {
            _notify("Could not open the session folder", ex.Message, NotificationSeverity.Warning);
        }
    }

    partial void OnSelectedNodeChanged(object? value)
    {
        if (value is SessionNodeViewModel node)
        {
            SessionActivated?.Invoke(this, node.Session);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();

        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = DebouncedSearchAsync(cts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
    }
}

using WhisperNotes.App.ViewModels;
using WhisperNotes.Core.Configuration;
using WhisperNotes.Core.Diarization;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.App.Shell;

/// <summary>
/// The shell's half of settings.json: the snapshot it reads from, the option records derived for
/// a run, and a debounced write-back of the two fields it owns.
/// </summary>
/// <remarks>
/// <para>
/// Saving is deliberately a read-modify-write of <see cref="AppSettings.Model"/> and
/// <see cref="AppSettings.DefaultProject"/> only. The AI panel and the Inputs page write the same
/// file from their own view models; persisting our whole in-memory snapshot would silently roll
/// their changes back, because the snapshot was loaded at startup and never sees them.
/// </para>
/// <para>
/// For the same reason the snapshot is refreshed — see <see cref="Refresh"/> — before anything
/// derives transcription or diarization options from it.
/// </para>
/// </remarks>
internal sealed class ShellSettings(ISettingsStore store, ShellNotificationCenter notifications)
    : IAsyncDisposable
{
    /// <summary>Long enough to swallow a burst of typing in the project box, short enough to feel saved.</summary>
    private const int SaveDebounceMs = 600;

    private AppSettings _current = new();
    private CancellationTokenSource? _debounce;

    public string SettingsPath => store.SettingsPath;

    public string? LastChannelId => _current.LastChannelId;

    public WhisperModelSize Model => _current.Model;

    public string? DefaultProject => _current.DefaultProject;

    public bool KeepSessionAudio => _current.KeepSessionAudio;

    public bool DiarizationEnabled => _current.Diarization.Enabled;

    public TranscriptionOptions Transcription => _current.ToTranscriptionOptions();

    public DiarizationOptions Diarization => _current.ToDiarizationOptions();

    public ChunkingOptions Chunking => _current.ToChunkingOptions();

    /// <summary>Startup read. An unreadable file costs the user their preferences, never the app.</summary>
    public void Initialize()
    {
        try
        {
            _current = store.Load();
        }
        catch (Exception ex)
        {
            _current = new AppSettings();
            notifications.Post(
                "Could not read settings",
                $"{ex.Message} Falling back to defaults.",
                NotificationSeverity.Warning);
        }
    }

    /// <summary>
    /// Re-reads the file. Unlike <see cref="Initialize"/> this does not swallow a failure: a run
    /// that cannot read its own configuration must not start on stale defaults.
    /// </summary>
    public void Refresh() => _current = store.Load();

    public void SetModel(WhisperModelSize model) => _current.Model = model;

    public void SetDefaultProject(string? project) => _current.DefaultProject = project;

    /// <summary>Coalesces a burst of edits into one write, replacing any write already pending.</summary>
    public void QueueSave()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();

        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = DebouncedSaveAsync(cts.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDebounceMs, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>Writes the shell's two fields into whatever is on disk right now. Never throws.</summary>
    public async Task SaveAsync()
    {
        try
        {
            var onDisk = store.Load();

            onDisk.Model = _current.Model;
            onDisk.DefaultProject = _current.DefaultProject;

            await store.SaveAsync(onDisk, CancellationToken.None).ConfigureAwait(true);

            // Keep our snapshot consistent with what is now on disk, so the next read of it
            // (e.g. Transcription) reflects changes made elsewhere.
            _current = onDisk;
        }
        catch (Exception ex)
        {
            notifications.Post("Could not save settings", ex.Message, NotificationSeverity.Warning);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_debounce is null)
        {
            return;
        }

        // CancelAsync rather than Cancel: registered continuations would otherwise run inline on
        // the UI thread, and this runs while the window is closing.
        await _debounce.CancelAsync().ConfigureAwait(true);
        _debounce.Dispose();
        _debounce = null;
    }
}

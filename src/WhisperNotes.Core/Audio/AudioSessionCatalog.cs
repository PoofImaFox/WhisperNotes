using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WhisperNotes.Core.Audio;

/// <summary>
/// One application that currently owns at least one WASAPI render session, collapsed across every
/// process it plays audio through.
/// </summary>
/// <param name="ExecutableName">
/// Lower-cased image name, e.g. <c>teams.exe</c>. This is the de-duplication key and the only part
/// that survives a restart, so it is what <see cref="ApplicationChannelId"/> persists.
/// </param>
/// <param name="DisplayName">Best human-readable label found across the collapsed sessions.</param>
/// <param name="ProcessId">
/// Pid of one live session for this executable, preferring a session that is actually rendering.
/// Valid only for as long as that process lives — always re-resolve before capturing.
/// </param>
/// <param name="ProcessCount">
/// Distinct processes that collapsed into this entry: 1 for a plain Win32 app, often a dozen for a
/// Chromium shell that spreads playback across helper processes.
/// </param>
/// <param name="IsActive">True when at least one of those sessions is currently rendering audio.</param>
internal sealed record AudioSessionApp(
    string ExecutableName,
    string DisplayName,
    int ProcessId,
    int ProcessCount,
    bool IsActive);

/// <summary>
/// Enumerates the applications holding a WASAPI render session, so the input picker can offer
/// "record Teams" rather than only "record whatever the speakers are playing".
/// </summary>
/// <remarks>
/// <para>
/// Sessions are keyed by <em>executable name</em>, not by pid. Chromium and Electron shells (Teams,
/// Chrome, Discord, Slack) open a render session per helper process, so a pid-keyed list would show
/// "Microsoft Teams" eight times, and each entry would name a pid that is recycled the moment the
/// helper exits. One entry per executable, carrying the pid of a session that is actually
/// <see cref="AudioSessionState.AudioSessionStateActive"/>, is both what the user expects to see and
/// the only thing worth attaching a capture to.
/// </para>
/// <para>
/// <see cref="AudioSessionState.AudioSessionStateInactive"/> sessions are deliberately kept: an app
/// that is open but momentarily silent (a paused video, a call between speakers) must stay pickable,
/// otherwise the input list flickers in and out under the user's cursor. Only
/// <see cref="AudioSessionState.AudioSessionStateExpired"/> sessions — whose owning process is gone —
/// are dropped.
/// </para>
/// <para>
/// Every step is best-effort. This runs against COM interfaces backed by processes that exit while
/// being enumerated, so a failure anywhere degrades to "fewer applications listed", never to an
/// exception escaping into the input picker.
/// </para>
/// </remarks>
internal static class AudioSessionCatalog
{
    /// <summary>Fallback label: the executable name itself.</summary>
    private const int NameRankExecutable = 0;

    /// <summary>The session's own <see cref="AudioSessionControl.DisplayName"/>, usually blank.</summary>
    private const int NameRankSession = 1;

    /// <summary>The process image name — always present, so this is the practical floor.</summary>
    private const int NameRankProcess = 2;

    /// <summary>The main window title, the only label that reads like the app's own name.</summary>
    private const int NameRankWindow = 3;

    /// <summary>
    /// Lists every application currently holding a render session, one entry per executable, sorted by
    /// display name.
    /// </summary>
    /// <remarks>
    /// Sorted alphabetically rather than active-first on purpose: the picker is refreshed while the
    /// user is looking at it, and an ordering that reshuffles whenever an app goes momentarily silent
    /// would move the row out from under the pointer.
    /// </remarks>
    public static IReadOnlyList<AudioSessionApp> GetApplications()
    {
        var apps = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var devices = new MMDeviceEnumerator();

            // Sessions live on the endpoint, so an app playing to a second sound card only shows up
            // there. Sweep every active render endpoint and let the executable key collapse an app
            // that is playing to more than one.
            foreach (MMDevice device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    Collect(device, apps);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One endpoint with a broken driver must not cost the user the whole app list.
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Enumerating endpoints is COM all the way down. A failure here means "no applications",
            // and whatever was collected before it is still worth showing.
        }

        List<AudioSessionApp> found = [.. apps.Values.Select(static entry => entry.ToApp())];
        found.Sort(static (left, right) =>
            string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));

        return found;
    }

    /// <summary>
    /// Re-resolves a persisted executable name to a live pid, preferring a session that is actually
    /// rendering. Returns null when the application is not running (or is running without audio).
    /// </summary>
    /// <remarks>
    /// Capture must call this instead of trusting the pid on a persisted
    /// <see cref="AudioChannel.ProcessId"/>: Windows recycles pids aggressively, so a stale id does not
    /// merely fail — it can name a completely unrelated process.
    /// </remarks>
    public static int? ResolveProcessId(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        string wanted = executableName.Trim();

        foreach (AudioSessionApp app in GetApplications())
        {
            if (string.Equals(app.ExecutableName, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return app.ProcessId;
            }
        }

        return null;
    }

    private static void Collect(MMDevice device, Dictionary<string, Entry> apps)
    {
        // MMDevice owns its AudioSessionManager and disposes it (which unregisters the session
        // notification NAudio registers on construction), so this must not be disposed here — the
        // caller's `device.Dispose()` is what releases it.
        SessionCollection? sessions = device.AudioSessionManager?.Sessions;
        if (sessions is null)
        {
            return;
        }

        for (int index = 0; index < sessions.Count; index++)
        {
            try
            {
                using AudioSessionControl session = sessions[index];

                if (Describe(session) is { } owner)
                {
                    Add(apps, owner);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A session can evaporate between reading Count and reading the indexer. Skipping it
                // is the whole recovery: the app it belonged to is on its way out anyway.
            }
        }
    }

    private static SessionOwner? Describe(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession)
        {
            // "System Sounds" is Windows' own notification mixer, not something a user can record.
            return null;
        }

        AudioSessionState state = session.State;
        if (state == AudioSessionState.AudioSessionStateExpired)
        {
            return null;
        }

        // Pid 0 is the system idle process; a session reporting it has no owner we could attach to.
        int processId = (int)session.GetProcessID;
        if (processId <= 0)
        {
            return null;
        }

        using Process? process = TryOpen(processId);
        if (process is null)
        {
            return null;
        }

        string? executable = ExecutableNameOf(process);
        if (executable is null)
        {
            return null;
        }

        (string name, int rank) = DisplayNameOf(process, session, executable);
        return new SessionOwner(
            executable,
            name,
            rank,
            processId,
            state == AudioSessionState.AudioSessionStateActive);
    }

    private static void Add(Dictionary<string, Entry> apps, SessionOwner owner)
    {
        if (!apps.TryGetValue(owner.ExecutableName, out Entry? entry))
        {
            entry = new Entry(owner.ExecutableName);
            apps[owner.ExecutableName] = entry;
        }

        entry.Add(owner);
    }

    private static Process? TryOpen(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process exited between the session enumeration and this call — routine on a Chromium
            // shell, which churns helper processes constantly. Drop the session, keep the sweep going.
            return null;
        }
    }

    private static string? ExecutableNameOf(Process process)
    {
        // ProcessName rather than MainModule.FileName: reading another process's module list throws
        // Win32Exception across an elevation or bitness boundary, and the image name is all an
        // application id needs.
        string? name = TryRead(() => process.ProcessName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim().ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    private static (string Name, int Rank) DisplayNameOf(
        Process process,
        AudioSessionControl session,
        string executable)
    {
        // MainWindowTitle first: it is the only label that says "Microsoft Teams" instead of "ms-teams".
        // It is empty for the windowless helper processes that usually own the audio, which is exactly
        // why the rank is carried through de-duplication rather than decided per session.
        string? title = TryRead(() => process.MainWindowTitle);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return (title.Trim(), NameRankWindow);
        }

        string? processName = TryRead(() => process.ProcessName);
        if (!string.IsNullOrWhiteSpace(processName))
        {
            return (processName.Trim(), NameRankProcess);
        }

        string? sessionName = TryRead(() => session.DisplayName);
        return string.IsNullOrWhiteSpace(sessionName)
            ? (executable, NameRankExecutable)
            : (sessionName.Trim(), NameRankSession);
    }

    /// <summary>
    /// Reads one property off a live process or session, yielding null instead of throwing.
    /// </summary>
    /// <remarks>
    /// Every accessor here can fail independently — the process exits (<c>InvalidOperationException</c>),
    /// or lives in a session this one cannot query (<c>Win32Exception</c>). A dead process must cost us
    /// one label, never the enumeration.
    /// </remarks>
    private static string? TryRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>One session's contribution, before it is folded into its executable's entry.</summary>
    private sealed record SessionOwner(
        string ExecutableName,
        string DisplayName,
        int NameRank,
        int ProcessId,
        bool IsActive);

    /// <summary>Accumulates every session belonging to one executable into a single listed entry.</summary>
    private sealed class Entry(string executableName)
    {
        private readonly HashSet<int> _processIds = [];

        public string ExecutableName { get; } = executableName;

        private string DisplayName { get; set; } = executableName;

        private int NameRank { get; set; } = NameRankExecutable;

        private int ProcessId { get; set; }

        private bool IsActive { get; set; }

        public void Add(SessionOwner owner)
        {
            _processIds.Add(owner.ProcessId);

            // Prefer the pid of a session that is actually rendering. On a Chromium shell the process
            // that owns the audio is a helper, and attaching to a silent sibling captures nothing.
            if (ProcessId == 0 || (owner.IsActive && !IsActive))
            {
                ProcessId = owner.ProcessId;
            }

            IsActive |= owner.IsActive;

            // The best label and the best pid usually come from different processes: the window title
            // belongs to the shell, the audio to a helper. Keep the best of each, independently.
            if (owner.NameRank > NameRank)
            {
                DisplayName = owner.DisplayName;
                NameRank = owner.NameRank;
            }
        }

        public AudioSessionApp ToApp() =>
            new(ExecutableName, DisplayName, ProcessId, _processIds.Count, IsActive);
    }
}

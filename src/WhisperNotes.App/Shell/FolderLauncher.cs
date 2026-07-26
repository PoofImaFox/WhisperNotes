using WhisperNotes.App.Services;
using WhisperNotes.App.ViewModels;

namespace WhisperNotes.App.Shell;

/// <summary>
/// Hands a directory to Explorer on the user's behalf and owns what happens when that fails.
/// </summary>
/// <remarks>
/// Failure is a banner, never an exception: every caller is a "here is where your work landed"
/// affordance attached to an already-successful operation, and none of them is worth unwinding a
/// finished recording for.
/// </remarks>
internal static class FolderLauncher
{
    public static void Open(string path, ShellNotificationCenter notifications)
    {
        try
        {
            SystemShell.OpenDirectory(path);
        }
        catch (Exception ex)
        {
            notifications.Post("Could not open that folder", ex.Message, NotificationSeverity.Warning);
        }
    }
}

using System.Diagnostics;

namespace NoteScribe.App.Services;

/// <summary>Explorer integration for the "where did my notes land" affordances.</summary>
internal static class SystemShell
{
    /// <summary>Opens a directory, creating it first so a not-yet-written session still reveals somewhere useful.</summary>
    public static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Start("explorer.exe", $"\"{path.TrimEnd(Path.DirectorySeparatorChar)}\"");
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void RevealFile(string path) => Start("explorer.exe", $"/select,\"{path}\"");

    private static void Start(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true })?.Dispose();
}

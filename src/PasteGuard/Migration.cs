using Microsoft.Win32;

namespace PasteGuard;

/// <summary>
/// One-time carry-over from the app's earlier name ("ClickFixBlocker"): settings, activity
/// history and the Start-with-Windows entry. Old files are copied, never deleted.
/// </summary>
internal static class Migration
{
    private const string OldName = "ClickFixBlocker";

    public static void Run()
    {
        // Logs first: copying anything else writes a log line, which would create a new log.
        var oldLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName);
        CopyIfMissing(Path.Combine(oldLogDir, "events.log.old"), Log.PathOnDisk + ".old");
        CopyIfMissing(Path.Combine(oldLogDir, "events.log"), Log.PathOnDisk);

        CopyIfMissing(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName, "config.json"),
            Settings.DefaultPath);

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue(OldName) is not null)
            {
                run.DeleteValue(OldName, throwOnMissingValue: false);
                Startup.Apply(true);
            }
        }
        catch (Exception ex)
        {
            Log.Write("ERROR", $"Could not migrate the startup entry: {ex.Message}");
        }
    }

    private static void CopyIfMissing(string from, string to)
    {
        try
        {
            if (!File.Exists(from) || File.Exists(to)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            Log.Write("MIGRATED", $"Copied {Path.GetFileName(from)} from the old {OldName} folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the app starts with defaults.
        }
    }
}

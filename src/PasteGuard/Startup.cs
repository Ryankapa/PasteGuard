using Microsoft.Win32;

namespace PasteGuard;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PasteGuard";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enable) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("ERROR", $"Could not update Start with Windows: {ex.Message}");
        }
    }

    /// <summary>Re-points an existing Run entry if the exe was moved since it was set.</summary>
    public static void Refresh(bool enabled)
    {
        if (!enabled) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var current = key?.GetValue(ValueName) as string;
        if (current is not null && current != Command) Apply(true);
    }
}

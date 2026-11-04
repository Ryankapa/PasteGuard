namespace PasteGuard;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Started by the previous version after an update: "--updated-from 1.0.0".
        var updatedFrom = ArgValue(args, UpdateInstaller.UpdatedFromArg);
        // Started as the update installer: "--install-to C:\...\PasteGuard.exe --install-from C:\...\PasteGuard-1.2.0.exe".
        var installTo = ArgValue(args, UpdateInstaller.InstallToArg);
        var installFrom = ArgValue(args, UpdateInstaller.InstallFromArg) ?? Environment.ProcessPath!;

        using var mutex = new Mutex(initiallyOwned: true, @"Local\PasteGuard.SingleInstance", out var firstInstance);
        var waitsForOldVersion = updatedFrom is not null || installTo is not null;
        if (!firstInstance && !(waitsForOldVersion && WaitForPreviousVersion(mutex)))
        {
            if (installTo is not null)
            {
                MessageBox.Show("PasteGuard couldn't finish updating because the previous version didn't close. Try the update again.",
                    "PasteGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MessageBox.Show(
                "PasteGuard is already running.\n\nLook for the shield icon in the system tray (click ^ next to the clock).",
                "PasteGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (installTo is not null)
        {
            Install(mutex, installFrom, installTo, updatedFrom ?? "an older version");
            return;
        }

        Migration.Run();
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp(updatedFrom));
    }

    /// <summary>The old version has exited: put the new exe in its place and start it.</summary>
    private static void Install(Mutex mutex, string source, string target, string updatedFrom)
    {
        ApplicationConfiguration.Initialize();
        // Let the installed copy take the single-instance lock as soon as it starts.
        string? error = UpdateInstaller.Install(source, target, updatedFrom,
            launch: (exe, from) => { ReleaseOnce(mutex); UpdateInstaller.Launch(exe, from); });
        if (error is null) return;

        Log.Write("ERROR", $"Update install failed: {error}");
        MessageBox.Show($"The update wasn't installed.\n\n{error}\n\nYou can also download the latest version from GitHub:\n{AppInfo.ReleasesUrl}",
            "PasteGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static bool _released;
    private static void ReleaseOnce(Mutex mutex)
    {
        if (_released) return;
        _released = true;
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>After an update, the old version is still closing; wait for it to let go.</summary>
    private static bool WaitForPreviousVersion(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(20)); }
        catch (AbandonedMutexException) { return true; } // it exited without releasing: ours now
    }
}

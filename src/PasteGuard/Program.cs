namespace PasteGuard;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Started by the previous version after an update: "--updated-from 1.0.0".
        var i = Array.IndexOf(args, UpdateInstaller.UpdatedFromArg);
        var updatedFrom = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        using var mutex = new Mutex(initiallyOwned: true, @"Local\PasteGuard.SingleInstance", out var firstInstance);
        if (!firstInstance && !(updatedFrom is not null && WaitForPreviousVersion(mutex)))
        {
            MessageBox.Show(
                "PasteGuard is already running.\n\nLook for the shield icon in the system tray (click ^ next to the clock).",
                "PasteGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Migration.Run();
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp(updatedFrom));
    }

    /// <summary>After an update, the old version is still closing; wait for it to let go.</summary>
    private static bool WaitForPreviousVersion(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(20)); }
        catch (AbandonedMutexException) { return true; } // it exited without releasing: ours now
    }
}

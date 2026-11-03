namespace PasteGuard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\PasteGuard.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show(
                "PasteGuard is already running.\n\nLook for the shield icon in the system tray (click ^ next to the clock).",
                "PasteGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Migration.Run();
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp());
    }
}

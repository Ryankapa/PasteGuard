using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PasteGuard;

/// <summary>Hidden message-only window that receives WM_CLIPBOARDUPDATE whenever the clipboard changes.</summary>
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public event Action? ClipboardChanged;

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams { Caption = "PasteGuard.ClipboardWatcher", Parent = HWND_MESSAGE });
        if (!AddClipboardFormatListener(Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start listening to the clipboard.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            // Handle it after this message returns, so the app that just copied isn't kept waiting.
            var ctx = SynchronizationContext.Current;
            if (ctx is not null) ctx.Post(_ => ClipboardChanged?.Invoke(), null);
            else ClipboardChanged?.Invoke();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

/// <summary>Clipboard helpers with retries (another app may briefly hold the clipboard open).</summary>
internal static class ClipboardUtil
{
    // Formats password managers set to ask clipboard tools to ignore their content.
    private static readonly string[] IgnoreFormats =
    {
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
    };

    public sealed record Snapshot(string Text, string? Html, string? Rtf);

    public static uint Sequence => GetClipboardSequenceNumber();

    /// <summary>Reads the clipboard text, or null if there is none or the source asked to be ignored.</summary>
    public static Snapshot? Read() => Retry<Snapshot>(() =>
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return null;
        foreach (var f in IgnoreFormats)
            if (data.GetDataPresent(f, false)) return null;
        if (!data.GetDataPresent(DataFormats.UnicodeText, true)) return null;
        if (data.GetData(DataFormats.UnicodeText, true) is not string text) return null;

        return new Snapshot(text, TryGetString(data, DataFormats.Html), TryGetString(data, DataFormats.Rtf));
    });

    public static bool Clear() => Retry<bool>(() => { Clipboard.Clear(); return true; });

    public static bool Restore(Snapshot snapshot) => Retry<bool>(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, snapshot.Text);
        if (snapshot.Html is not null) data.SetData(DataFormats.Html, snapshot.Html);
        if (snapshot.Rtf is not null) data.SetData(DataFormats.Rtf, snapshot.Rtf);
        Clipboard.SetDataObject(data, copy: true);
        return true;
    });

    private static string? TryGetString(IDataObject data, string format)
    {
        try { return data.GetDataPresent(format, false) ? data.GetData(format, false) as string : null; }
        catch { return null; }
    }

    private static T? Retry<T>(Func<T?> action)
    {
        for (var i = 0; ; i++)
        {
            try { return action(); }
            catch (ExternalException) when (i < 9) { Thread.Sleep(25); }
            catch (ExternalException) { return default; }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}

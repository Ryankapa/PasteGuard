using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PasteGuard;

/// <summary>"Check for updates" window: checks GitHub, shows what's new, downloads, verifies and installs.</summary>
internal sealed class UpdateForm : Form
{
    private static UpdateForm? _open;

    private readonly Action _exitForUpdate;
    private readonly UpdateChecker _checker = new();
    private readonly Label _title;
    private readonly Label _status;
    private readonly TextBox _notes;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _close;
    private readonly LinkLabel _page;
    private CancellationTokenSource? _cts;
    private UpdateInfo? _update;
    private bool _busy;

    /// <summary>Opens the window and starts a check, or brings the existing window to the front.</summary>
    /// <param name="exitForUpdate">Closes this instance once the new version has been started.</param>
    public static void ShowSingle(Icon? icon, Action exitForUpdate)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Activate();
            return;
        }
        _open = new UpdateForm(exitForUpdate) { Icon = icon };
        _open.FormClosed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private UpdateForm(Action exitForUpdate)
    {
        _exitForUpdate = exitForUpdate;

        Text = "PasteGuard: Check for updates";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        Controls.Add(root);

        _title = new Label { AutoSize = true, Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
        _status = new Label { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 8) };
        _notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 480,
            Height = 170,
            BackColor = SystemColors.Window,
            Visible = false,
            Margin = new Padding(0, 0, 0, 8),
        };
        _progress = new ProgressBar { Width = 480, Height = 18, Visible = false, Margin = new Padding(0, 0, 0, 8) };
        _page = new LinkLabel { Text = "View this release on GitHub", AutoSize = true, Visible = false, Margin = new Padding(0, 0, 0, 4) };
        _page.LinkClicked += (_, _) => OpenUrl(_update?.PageUrl ?? AppInfo.ReleasesUrl);

        _primary = new Button { AutoSize = true, MinimumSize = new Size(150, 0), Visible = false };
        _primary.Click += async (_, _) => await OnPrimaryAsync();
        _close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 0) };
        _close.Click += (_, _) => Close();
        CancelButton = _close;

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0), Anchor = AnchorStyles.Right };
        buttons.Controls.AddRange(new Control[] { _primary, _close });

        root.Controls.AddRange(new Control[] { _title, _status, _notes, _progress, _page, buttons });

        Shown += async (_, _) => await CheckAsync();
        FormClosing += (_, _) => _cts?.Cancel();
        FormClosed += (_, _) => { _cts?.Dispose(); _checker.Dispose(); };
    }

    private async Task CheckAsync()
    {
        _update = null;
        SetMessage("Checking for updates…", $"You have version {AppInfo.VersionText}. Asking GitHub for the latest release…");
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = true;
        _primary.Visible = false;
        _cts = new CancellationTokenSource();
        try
        {
            _update = await _checker.CheckAsync(AppInfo.Version, _cts.Token);
            _progress.Visible = false;
            if (_update is null)
            {
                SetMessage("You're up to date", $"PasteGuard {AppInfo.VersionText} is the latest version.");
                Log.Write("UPDATE", $"Checked for updates: {AppInfo.VersionText} is the latest");
                return;
            }

            SetMessage($"PasteGuard {_update.VersionText} is available",
                $"You have version {AppInfo.VersionText}. The update downloads from GitHub, is checked against its SHA-256 hash, " +
                "and PasteGuard restarts on the new version. Your settings and history are kept.");
            _notes.Text = FormatNotes(_update.Notes);
            _notes.Visible = _notes.Text.Length > 0;
            _page.Visible = true;
            SetPrimary("Download and install");
            _close.Text = "Later";
            Log.Write("UPDATE", $"Update available: {_update.VersionText} (have {AppInfo.VersionText})");
        }
        catch (OperationCanceledException) { }
        catch (UpdateException ex)
        {
            Fail("Couldn't check for updates", ex.Message, retry: true);
        }
    }

    private async Task OnPrimaryAsync()
    {
        if (_update is null) { await CheckAsync(); return; }
        await InstallAsync(_update);
    }

    private async Task InstallAsync(UpdateInfo update)
    {
        var exe = UpdateInstaller.CurrentExe;
        if (exe is null)
        {
            Fail("Can't update this copy", "This copy of PasteGuard isn't running from PasteGuard.exe (for example, it was started from source). " +
                "Download the new version from GitHub instead.", retry: false);
            return;
        }

        _busy = true;
        _primary.Visible = false;
        _close.Text = "Cancel";
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 0;
        _progress.Visible = true;
        SetMessage($"Downloading PasteGuard {update.VersionText}…", update.ExeSize > 0
            ? $"Downloading from GitHub (about {Math.Max(1, update.ExeSize / 1_000_000)} MB)."
            : "Downloading from GitHub.");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));
            var downloaded = await _checker.DownloadAsync(update, UpdateInstaller.DownloadFolder, progress, _cts.Token);

            SetMessage($"Installing PasteGuard {update.VersionText}…", "The download matches its SHA-256 hash. PasteGuard will restart in a moment.");
            _close.Enabled = false;
            try
            {
                // A copy of this exe swaps in the new version once we've exited (see UpdateInstaller).
                UpdateInstaller.StartInstaller(downloaded, exe);
            }
            catch (Exception ex) when (UpdateInstaller.IsLaunchError(ex))
            {
                throw new UpdateException(
                    "The installer couldn't be started, so nothing was changed.\n\n" +
                    $"Windows said: {UpdateInstaller.Describe(ex)}", ex);
            }

            Log.Write("UPDATE", $"Installed {update.VersionText} (from {AppInfo.VersionText}); restarting");
            _busy = false;
            Close();
            _exitForUpdate();
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            Log.Write("UPDATE", "Download cancelled");
        }
        catch (UpdateException ex)
        {
            _busy = false;
            Log.Write("ERROR", $"Update to {update.VersionText} failed: {ex.Message}");
            Fail("The update wasn't installed", ex.Message, retry: true);
        }
    }

    private void Fail(string title, string message, bool retry)
    {
        _progress.Visible = false;
        _close.Enabled = true;
        _close.Text = "Close";
        SetMessage(title, message + "\n\nYou can also download the latest version from GitHub.");
        _page.Visible = true;
        if (retry) SetPrimary("Try again");
        else _primary.Visible = false;
    }

    private void SetPrimary(string text)
    {
        _primary.Text = text;
        _primary.Visible = true;
        AcceptButton = _primary;
        _primary.Focus();
    }

    private void SetMessage(string title, string status)
    {
        _title.Text = title;
        _status.Text = status;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing mid-download cancels it; once installing has started the window stays until restart.
        if (_busy && !_close.Enabled) e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// <summary>Turns the release's Markdown notes into readable plain text.</summary>
    internal static string FormatNotes(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var l = Regex.Replace(line, @"^\s*#{1,6}\s*", "");
            l = Regex.Replace(l, @"^(\s*)[-*]\s+", "$1• ");
            l = Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return l.Replace("**", "").Replace("`", "");
        });
        return string.Join("\r\n", lines).Trim();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser */ }
    }
}

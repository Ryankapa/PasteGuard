using System.Diagnostics;

namespace PasteGuard;

/// <summary>The tray agent: shield icon in the notification area plus its right-click menu.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly JevClient _jev;
    private readonly Analyzer _analyzer;
    private readonly ClipboardWatcher _watcher;
    private readonly NotifyIcon _tray;
    private readonly Dictionary<AgentState, Icon> _icons = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _reviewItem;
    private AgentState _apiState = AgentState.Protecting;
    private SettingsForm? _settingsForm;
    private ReviewForm? _reviewForm;
    private bool _balloonOpensReview;

    /// <param name="updatedFrom">The previous version, when this start follows an update.</param>
    public TrayApp(string? updatedFrom = null)
    {
        _settings = Settings.Load();
        var firstRun = !Settings.Exists();
        if (firstRun) _settings.Save();

        _jev = new JevClient(_settings.ApiBaseUrl);
        _analyzer = new Analyzer(() => _settings, _jev);

        foreach (var state in Enum.GetValues<AgentState>()) _icons[state] = IconFactory.For(state);

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Protection on", null, (_, _) => ToggleEnabled()) { CheckOnClick = false };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        _reviewItem = new ToolStripMenuItem("Review last blocked command…", null, (_, _) => ShowReview()) { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_reviewItem);
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("Threat activity…", null, (_, _) => ActivityForm.ShowSingle(_icons[AgentState.Protecting])));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Check for updates…", null, (_, _) => ShowUpdates()));
        menu.Items.Add(new ToolStripMenuItem($"About PasteGuard {AppInfo.VersionText}", null, (_, _) => OpenUrl(AppInfo.RepoUrl)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => { if (_balloonOpensReview) ShowReview(); };

        _analyzer.Notify += (title, text, icon, opensReview) =>
        {
            _balloonOpensReview = opensReview;
            _tray.ShowBalloonTip(8000, title, text, icon);
        };
        _analyzer.StateChanged += state => { _apiState = state; RefreshUi(); };
        _analyzer.LastBlockedChanged += () => _reviewItem.Enabled = _analyzer.LastBlocked is not null;

        _watcher = new ClipboardWatcher();
        _watcher.ClipboardChanged += _analyzer.OnClipboardChanged;

        Startup.Refresh(_settings.StartWithWindows);
        RefreshUi();

        Log.Write("START", $"version={AppInfo.VersionText} threshold={_settings.Threshold} enabled={_settings.Enabled}");
        _ = UpdateInstaller.CleanupAsync(UpdateInstaller.CurrentExe, justUpdated: updatedFrom is not null);

        if (updatedFrom is not null)
        {
            Log.Write("UPDATE", $"Now running {AppInfo.VersionText} (updated from {updatedFrom})");
            _balloonOpensReview = false;
            _tray.ShowBalloonTip(5000, "PasteGuard updated",
                $"You're now on version {AppInfo.VersionText}. Protection is back on.", ToolTipIcon.Info);
        }

        if (!_settings.HasApiKey) ShowSettings();
    }

    private void ShowUpdates() => UpdateForm.ShowSingle(_icons[AgentState.Protecting], ExitThread);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser */ }
    }

    private AgentState CurrentState =>
        !_settings.Enabled ? AgentState.Paused :
        !_settings.HasApiKey ? AgentState.NoKey :
        _apiState == AgentState.ApiError ? AgentState.ApiError :
        AgentState.Protecting;

    private void RefreshUi()
    {
        var state = CurrentState;
        var status = state switch
        {
            AgentState.Protecting => $"Protecting (block at {_settings.Threshold}+)",
            AgentState.Paused => "Paused",
            AgentState.NoKey => "No Jev API key: open Settings",
            _ => "Can't reach Jev: check Settings",
        };
        _tray.Icon = _icons[state];
        _tray.Text = Truncate($"PasteGuard: {status}", 127);
        _statusItem.Text = status;
        _enabledItem.Checked = _settings.Enabled;
        _startupItem.Checked = _settings.StartWithWindows;
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        SaveAndRefresh();
        Log.Write(_settings.Enabled ? "RESUMED" : "PAUSED", "");
    }

    private void ToggleStartup()
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        Startup.Apply(_settings.StartWithWindows);
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Log.Write("ERROR", $"Could not save settings: {ex.Message}"); }
        RefreshUi();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings, _jev) { Icon = _icons[AgentState.Protecting] };
        _settingsForm.Saved += () =>
        {
            Startup.Apply(_settings.StartWithWindows);
            _apiState = AgentState.Protecting;
            SaveAndRefresh();
            Log.Write("SETTINGS", $"threshold={_settings.Threshold} onApiError={_settings.OnApiError} startWithWindows={_settings.StartWithWindows}");
        };
        _settingsForm.CheckForUpdatesRequested += ShowUpdates;
        _settingsForm.KeySaved += () =>
        {
            _apiState = AgentState.Protecting;
            SaveAndRefresh();
            Log.Write("SETTINGS", "API key tested and saved");
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void ShowReview()
    {
        var item = _analyzer.LastBlocked;
        if (item is null) return;
        if (_reviewForm is { IsDisposed: false })
        {
            _reviewForm.Activate();
            return;
        }

        using (_reviewForm = new ReviewForm(item) { Icon = _icons[AgentState.ApiError] })
        {
            if (_reviewForm.ShowDialog() != DialogResult.OK) return;

            if (_analyzer.Release(item, _reviewForm.AlwaysAllow, _settings))
            {
                _tray.ShowBalloonTip(4000, "Command released",
                    _reviewForm.AlwaysAllow
                        ? "It's back on your clipboard and won't be blocked again."
                        : "It's back on your clipboard. You can paste it now.",
                    ToolTipIcon.Info);
                _balloonOpensReview = false;
            }
            else
            {
                MessageBox.Show("Couldn't put the command back on the clipboard. Please try again.",
                    "PasteGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        _reviewForm = null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    protected override void ExitThreadCore()
    {
        Log.Write("STOP", "");
        _watcher.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _settingsForm?.Dispose();
        _reviewForm?.Dispose();
        _jev.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        base.ExitThreadCore();
    }
}

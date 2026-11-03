using System.Diagnostics;

namespace PasteGuard;

/// <summary>Threat activity: a table of blocked and released commands, read from the local log.</summary>
internal sealed class ActivityForm : Form
{
    private static ActivityForm? _open;

    private readonly ListView _list;
    private readonly CheckBox _showAll;
    private readonly Label _summary;

    /// <summary>Opens the window, or brings the existing one to the front.</summary>
    public static void ShowSingle(Icon? icon)
    {
        if (_open is { IsDisposed: false })
        {
            _open.Reload();
            _open.Activate();
            return;
        }
        _open = new ActivityForm { Icon = icon };
        _open.Show();
    }

    private ActivityForm()
    {
        Text = "PasteGuard: threat activity";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 480);
        MinimumSize = new Size(560, 300);
        Padding = new Padding(10);

        _summary = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Time", 140);
        _list.Columns.Add("Action", 170);
        _list.Columns.Add("Score", 60, HorizontalAlignment.Right);
        _list.Columns.Add("Command / details", 440);
        _list.DoubleClick += (_, _) => ShowSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ShowSelected(); };
        _list.Resize += (_, _) => FitLastColumn();

        _showAll = new CheckBox { Text = "Show all events (allowed, errors, start/stop)", AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
        _showAll.CheckedChanged += (_, _) => Reload();

        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => Reload();
        var openRaw = new Button { Text = "Open raw log", AutoSize = true };
        openRaw.Click += (_, _) => OpenRawLog();
        var clear = new Button { Text = "Clear history…", AutoSize = true };
        clear.Click += (_, _) => ClearHistory();
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        CancelButton = close;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.AddRange(new Control[] { _showAll, refresh, openRaw, clear, close });

        Controls.Add(_list);
        Controls.Add(_summary);
        Controls.Add(bottom);

        Load += (_, _) => Reload();
        FormClosed += (_, _) => _open = null;
    }

    private void Reload()
    {
        var all = Log.ReadEntries();
        var threats = all.Where(e => e.IsThreat).ToList();
        var since = DateTime.Now.AddDays(-30);
        var blocked30 = threats.Count(e => e.Kind.StartsWith("BLOCKED", StringComparison.Ordinal) && e.Time >= since);
        var released30 = threats.Count(e => e.Kind.StartsWith("RELEASED", StringComparison.Ordinal) && e.Time >= since);

        _summary.Text = $"Last 30 days: {blocked30} blocked, {released30} released by you.   Double-click a row to see the full command.";

        var rows = (_showAll.Checked ? all : threats).AsEnumerable().Reverse(); // newest first
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in rows)
        {
            var item = new ListViewItem(e.Time.ToString("yyyy-MM-dd HH:mm:ss")) { Tag = e };
            item.SubItems.Add(e.Action);
            item.SubItems.Add(e.Score?.ToString() ?? "");
            item.SubItems.Add(e.Text ?? e.Message);
            if (e.Kind.StartsWith("BLOCKED", StringComparison.Ordinal)) item.ForeColor = Color.Firebrick;
            else if (e.Kind.StartsWith("RELEASED", StringComparison.Ordinal)) item.ForeColor = Color.DarkGoldenrod;
            else if (e.Kind == "ERROR") item.ForeColor = SystemColors.GrayText;
            _list.Items.Add(item);
        }
        if (_list.Items.Count == 0)
        {
            var empty = new ListViewItem("") { ForeColor = SystemColors.GrayText };
            empty.SubItems.Add("No threats recorded yet.");
            _list.Items.Add(empty);
        }
        _list.EndUpdate();
        FitLastColumn();
    }

    private void FitLastColumn()
    {
        if (_list.Columns.Count < 4) return;
        var used = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width;
        _list.Columns[3].Width = Math.Max(200, _list.ClientSize.Width - used - 4);
    }

    private void ShowSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not LogEntry e) return;
        var details =
            $"{e.Action} at {e.Time:yyyy-MM-dd HH:mm:ss}" +
            (e.Score is int s ? $"\nRisk score: {s}/100" : "") +
            $"\n\n{e.Text ?? e.Message}" +
            (e.Text is not null && e.Text.EndsWith("…") ? "\n\n(Shortened in the log to the first 300 characters.)" : "") +
            (e.Kind.StartsWith("BLOCKED", StringComparison.Ordinal) ? "\n\nDon't run this command. It's shown here for reference only." : "");
        MessageBox.Show(this, details, "PasteGuard: event details", MessageBoxButtons.OK,
            e.Kind.StartsWith("BLOCKED", StringComparison.Ordinal) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void ClearHistory()
    {
        if (MessageBox.Show(this, "Delete the whole activity history? This can't be undone.", "PasteGuard",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Log.Clear();
        Reload();
    }

    private static void OpenRawLog()
    {
        if (!File.Exists(Log.PathOnDisk)) Log.Write("INFO", "Log created.");
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.PathOnDisk}\"") { UseShellExecute = true }); }
        catch { }
    }
}

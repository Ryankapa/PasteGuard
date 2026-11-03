namespace PasteGuard;

/// <summary>
/// Shown when the user clicks a "Blocked" alert. Shows the full command and lets the user
/// put it back on the clipboard after confirming. "Keep blocked" is the default button.
/// </summary>
internal sealed class ReviewForm : Form
{
    public bool AlwaysAllow => _alwaysAllow.Checked;

    private readonly CheckBox _alwaysAllow;

    public ReviewForm(BlockedItem item)
    {
        Text = "PasteGuard: review blocked command";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        Controls.Add(root);

        var why = item.Score is int score
            ? $"Blocked at {item.At:HH:mm} because its risk score was {score}/100 (your threshold is {item.Threshold})."
            : $"Blocked at {item.At:HH:mm} because {item.Reason}.";

        root.Controls.Add(new Label
        {
            Text = "⚠  This command was removed from your clipboard",
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size * 1.2f, FontStyle.Bold),
            ForeColor = Color.Firebrick,
            Margin = new Padding(0, 0, 0, 6),
        });
        root.Controls.Add(Text_(why));

        root.Controls.Add(new TextBox
        {
            Text = item.Snapshot.Text,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            Width = 520,
            Height = 110,
            Font = new Font(FontFamily.GenericMonospace, Font.Size),
            Margin = new Padding(0, 8, 0, 8),
            TabStop = false,
        });

        root.Controls.Add(Text_(
            "Only release it if YOU chose to copy this from a source you trust, such as an official install page.\n\n" +
            "If a website, pop-up, CAPTCHA or \"fix\" message told you to paste this into Run (Win+R), a terminal or " +
            "PowerShell, it's a scam. Keep it blocked and close that page."));

        _alwaysAllow = new CheckBox
        {
            Text = "Always allow this exact command (don't block it again)",
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        root.Controls.Add(_alwaysAllow);

        var keep = new Button { Text = "Keep blocked", AutoSize = true, MinimumSize = new Size(120, 30), DialogResult = DialogResult.Cancel };
        var release = new Button { Text = "Release to clipboard", AutoSize = true, MinimumSize = new Size(150, 30), DialogResult = DialogResult.OK };
        AcceptButton = keep;
        CancelButton = keep;

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.Add(keep);
        buttons.Controls.Add(release);
        root.Controls.Add(buttons);

        Shown += (_, _) => keep.Focus();
    }

    private static Label Text_(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(520, 0),
    };
}

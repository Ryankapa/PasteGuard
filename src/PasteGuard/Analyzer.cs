namespace PasteGuard;

public enum AgentState { Protecting, Paused, NoKey, ApiError }

/// <summary>A command the agent removed from the clipboard, kept so the user can review and release it.</summary>
internal sealed record BlockedItem(ClipboardUtil.Snapshot Snapshot, int? Score, int Threshold, DateTime At, string Reason);

/// <summary>
/// The main pipeline for every clipboard change:
/// read text → local pre-filter → hold (clear clipboard) → ask Jev → keep blocked or restore.
/// Runs on the UI thread; awaits return to it.
/// </summary>
internal sealed class Analyzer
{
    public const int MaxChars = 16_000;
    private static readonly TimeSpan ErrorNoticeInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckingNoticeDelay = TimeSpan.FromSeconds(1);

    private readonly Func<Settings> _settings;
    private readonly JevClient _jev;
    private DateTime _lastErrorNotice = DateTime.MinValue;

    // Fingerprint of the text we last put back on the clipboard. Windows reports our own
    // put-back as a new copy (often more than once), so without this we would re-check our
    // own text forever. Cleared as soon as the user copies something different.
    private string? _restoredFingerprint;

    /// <summary>title, text, icon, and whether clicking it should open the review window.</summary>
    public event Action<string, string, ToolTipIcon, bool>? Notify;
    public event Action<AgentState>? StateChanged;
    public event Action? LastBlockedChanged;

    /// <summary>The most recent blocked command, until it is released or replaced.</summary>
    public BlockedItem? LastBlocked { get; private set; }

    public Analyzer(Func<Settings> settings, JevClient jev)
    {
        _settings = settings;
        _jev = jev;
    }

    public async void OnClipboardChanged()
    {
        try
        {
            await HandleAsync();
        }
        catch (Exception ex)
        {
            Log.Write("ERROR", $"Unexpected: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleAsync()
    {
        var s = _settings();
        if (!s.Enabled) return;

        // Our own "clear" leaves no text, so it ends here too.
        var snapshot = ClipboardUtil.Read();
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text) || snapshot.Text.Length > MaxChars) return;

        var fingerprint = Settings.Fingerprint(snapshot.Text);
        if (fingerprint == _restoredFingerprint) return; // text we put back ourselves
        _restoredFingerprint = null;

        var pre = PreFilter.Check(snapshot.Text);
        if (!pre.IsCommandLike) return;
        if (s.IsAllowed(snapshot.Text)) return; // the user chose "Always allow" for this exact command

        // Hold: take it off the clipboard right away so it can't be pasted before the verdict.
        if (!ClipboardUtil.Clear()) return;
        var heldSeq = ClipboardUtil.Sequence;

        using var checking = new CancellationTokenSource();
        _ = ShowCheckingIfSlowAsync(checking.Token);

        int score;
        try
        {
            score = await _jev.ScoreAsync(s.ApiKey, s.Model, snapshot.Text);
        }
        catch (JevException ex)
        {
            checking.Cancel();
            HandleApiError(s, snapshot, heldSeq, pre, ex);
            return;
        }
        checking.Cancel();
        StateChanged?.Invoke(AgentState.Protecting);

        var indicators = string.Join(",", pre.Indicators);
        if (score >= s.Threshold)
        {
            Log.Write("BLOCKED", $"score={score} threshold={s.Threshold} indicators={indicators} text=\"{TextPrep.Preview(snapshot.Text, 300)}\"");
            SetLastBlocked(new BlockedItem(snapshot, score, s.Threshold, DateTime.Now, $"risk score {score}/100"));
            Notify?.Invoke(
                "Blocked a dangerous command",
                $"A copied command was removed from your clipboard (risk {score}/100). " +
                "If a website told you to paste it, it's a scam. Click here to review.",
                ToolTipIcon.Warning, true);
            return;
        }

        Log.Write("ALLOWED", $"score={score} threshold={s.Threshold} indicators={indicators}");
        RestoreIfUntouched(snapshot, heldSeq);
    }

    private void HandleApiError(Settings s, ClipboardUtil.Snapshot snapshot, uint heldSeq, PreFilterResult pre, JevException ex)
    {
        Log.Write("ERROR", $"{ex.Message} status={ex.StatusCode?.ToString() ?? "-"}");
        StateChanged?.Invoke(s.HasApiKey ? AgentState.ApiError : AgentState.NoKey);

        if (s.OnApiError == ApiErrorAction.Block)
        {
            Log.Write("BLOCKED_UNCHECKED", $"indicators={string.Join(",", pre.Indicators)} text=\"{TextPrep.Preview(snapshot.Text, 300)}\"");
            SetLastBlocked(new BlockedItem(snapshot, null, s.Threshold, DateTime.Now, "Jev couldn't be reached to check it"));
            Notify?.Invoke("Blocked a copied command (not checked)",
                $"Jev couldn't be reached, so a command-like text was removed from your clipboard. Click here to review.",
                ToolTipIcon.Warning, true);
            return;
        }

        RestoreIfUntouched(snapshot, heldSeq);
        if (DateTime.Now - _lastErrorNotice > ErrorNoticeInterval)
        {
            _lastErrorNotice = DateTime.Now;
            Notify?.Invoke("Couldn't check a copied command",
                $"{ex.Message} Your copied text was put back. Open Settings from the tray icon to fix this.",
                ToolTipIcon.Error, false);
        }
    }

    /// <summary>Puts a blocked command back on the clipboard after the user confirmed it.</summary>
    public bool Release(BlockedItem item, bool alwaysAllow, Settings settings)
    {
        if (!PutBack(item.Snapshot)) return false;

        if (alwaysAllow)
        {
            settings.Allow(item.Snapshot.Text);
            try { settings.Save(); }
            catch (Exception ex) { Log.Write("ERROR", $"Could not save allowed command: {ex.Message}"); }
        }

        Log.Write(alwaysAllow ? "RELEASED_ALWAYS" : "RELEASED",
            $"score={item.Score?.ToString() ?? "-"} text=\"{TextPrep.Preview(item.Snapshot.Text, 300)}\"");
        if (ReferenceEquals(item, LastBlocked)) SetLastBlocked(null);
        return true;
    }

    private void SetLastBlocked(BlockedItem? item)
    {
        LastBlocked = item;
        LastBlockedChanged?.Invoke();
    }

    private void RestoreIfUntouched(ClipboardUtil.Snapshot snapshot, uint heldSeq)
    {
        // If the user copied something else while we were checking, don't overwrite it.
        if (ClipboardUtil.Sequence != heldSeq) return;
        PutBack(snapshot);
    }

    private bool PutBack(ClipboardUtil.Snapshot snapshot)
    {
        _restoredFingerprint = Settings.Fingerprint(snapshot.Text); // set first: the change event follows
        return ClipboardUtil.Restore(snapshot);
    }

    private async Task ShowCheckingIfSlowAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(CheckingNoticeDelay, ct);
            Notify?.Invoke("Checking copied command…", "Wait a moment before pasting.", ToolTipIcon.Info, false);
        }
        catch (OperationCanceledException) { }
    }
}

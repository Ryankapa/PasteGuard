using System.Globalization;
using System.Text.RegularExpressions;

namespace PasteGuard;

/// <summary>One parsed line of events.log.</summary>
public sealed record LogEntry(DateTime Time, string Kind, string Message)
{
    private static readonly Regex LineRx = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s{2}(\S+)\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex ScoreRx = new(@"\bscore=(\d+)", RegexOptions.Compiled);
    private static readonly Regex TextRx = new(@"\btext=""(.*)""\s*$", RegexOptions.Compiled);

    /// <summary>Kinds that describe a threat decision (shown by default in Threat activity).</summary>
    public static readonly string[] ThreatKinds = { "BLOCKED", "BLOCKED_UNCHECKED", "RELEASED", "RELEASED_ALWAYS" };

    public bool IsThreat => ThreatKinds.Contains(Kind);

    public int? Score => ScoreRx.Match(Message) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    public string? Text => TextRx.Match(Message) is { Success: true } m ? m.Groups[1].Value : null;

    public string Action => Kind switch
    {
        "BLOCKED" => "Blocked",
        "BLOCKED_UNCHECKED" => "Blocked (not checked)",
        "RELEASED" => "Released by you",
        "RELEASED_ALWAYS" => "Released + always allow",
        "ALLOWED" => "Allowed",
        "ERROR" => "Error",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(Kind.ToLowerInvariant()),
    };

    public static bool TryParse(string line, out LogEntry entry)
    {
        entry = null!;
        var m = LineRx.Match(line);
        if (!m.Success ||
            !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;
        entry = new LogEntry(time, m.Groups[2].Value, m.Groups[3].Value.Trim());
        return true;
    }
}

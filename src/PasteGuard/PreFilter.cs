using System.Text.RegularExpressions;

namespace PasteGuard;

public sealed record PreFilterResult(bool IsCommandLike, IReadOnlyList<string> Indicators);

/// <summary>
/// Local, offline check that decides whether copied text looks like a command.
/// Only text that passes is sent to Jev, so ordinary clipboard content never leaves the PC.
/// </summary>
public static class PreFilter
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Any one of these makes the text "command-like".
    private static readonly (string Name, Regex Pattern)[] Gates =
    {
        ("powershell",      new(@"\b(powershell|pwsh)(\.exe)?\b", Opts)),
        ("cmd",             new(@"\bcmd(\.exe)?\s+/[ckr]\b", Opts)),
        ("mshta",           new(@"\bmshta(\.exe)?\b", Opts)),
        ("iex",             new(@"\b(iex|invoke-expression)\b", Opts)),
        ("download",        new(@"\b(irm|iwr|invoke-restmethod|invoke-webrequest|downloadstring|downloadfile|downloaddata|net\.webclient|start-bitstransfer)\b", Opts)),
        ("curl-wget-url",   new(@"\b(curl|wget)(\.exe)?\b[^\r\n]*?\bhttps?://", Opts)),
        ("encoded",         new(@"\s-(e|ec|en|enc|enco|encod|encodedcommand)\s+[A-Za-z0-9+/=]{20,}", Opts)),
        ("frombase64",      new(@"frombase64string", Opts)),
        ("certutil",        new(@"\bcertutil(\.exe)?\b[^\r\n]*-(urlcache|decode|decodehex)", Opts)),
        ("bitsadmin",       new(@"\bbitsadmin(\.exe)?\b", Opts)),
        ("rundll32",        new(@"\brundll32(\.exe)?\b", Opts)),
        ("regsvr32",        new(@"\bregsvr32(\.exe)?\b", Opts)),
        ("msiexec-url",     new(@"\bmsiexec(\.exe)?\b[^\r\n]*?(https?://|\\\\)", Opts)),
        ("wscript",         new(@"\b(wscript|cscript)(\.exe)?\b", Opts)),
        ("hidden-window",   new(@"\s-(w|wi|win|wind|windo|window|windowstyle)\s+(h|hi|hid|hidden|1)\b", Opts)),
        ("start-process",   new(@"\bstart-process\b", Opts)),
        ("finger",          new(@"\bfinger(\.exe)?\s+\S+@\S+", Opts)),
        ("conhost",         new(@"\bconhost(\.exe)?\b[^\r\n]*--headless", Opts)),
        ("lolbin",          new(@"\b(syncappvpublishingserver|pcalua|forfiles|scriptrunner|cmstp|installutil|regasm|regsvcs|msbuild|odbcconf|ieexec)(\.exe)?\b", Opts)),
        ("unc-exec",        new(@"(\bstart\b|\bcall\b|&)\s*\\\\[\w.\-@]+\\", Opts)),
        ("bypass",          new(@"-(ep|exec|executionpolicy)\s+(bypass|unrestricted)\b", Opts)),
        ("obfuscation",     new(@"(\^[a-z]){4,}|(`[a-z]){4,}", Opts)),
    };

    // Extra signals that are logged, but don't make text command-like on their own.
    private static readonly (string Name, Regex Pattern)[] Signals =
    {
        ("fake-verification", new(@"#[^\r\n]*\b(not a robot|i am human|i'm not a robot|verification|verify|captcha|cloudflare|ray id|human check)\b", Opts)),
        ("padding",           new(@"[ \t ]{20,}", Opts)),
        ("ip-url",            new(@"https?://\d{1,3}(\.\d{1,3}){3}", Opts)),
    };

    public static PreFilterResult Check(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, Array.Empty<string>());

        var normalized = Deobfuscate(text);
        var hits = new List<string>();
        foreach (var (name, pattern) in Gates)
        {
            if (pattern.IsMatch(normalized) || pattern.IsMatch(text)) hits.Add(name);
        }

        if (hits.Count == 0) return new(false, Array.Empty<string>());

        foreach (var (name, pattern) in Signals)
        {
            if (pattern.IsMatch(text)) hits.Add(name);
        }
        return new(true, hits);
    }

    /// <summary>Removes common cmd/PowerShell escape tricks (p^o^w^e^r, pow`ers`hell, 'pow'+'ershell').</summary>
    internal static string Deobfuscate(string text)
    {
        var s = text.Replace("^", "").Replace("`", "");
        s = Regex.Replace(s, @"['""]\s*\+\s*['""]", "");
        return s;
    }
}

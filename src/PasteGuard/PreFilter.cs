using System.Text;
using System.Text.RegularExpressions;

namespace PasteGuard;

public sealed record PreFilterResult(bool IsCommandLike, IReadOnlyList<string> Indicators);

/// <summary>
/// Local, offline check that decides whether copied text looks like a command.
/// Only text that passes is sent to Jev, so ordinary clipboard content never leaves the PC.
/// Patterns are case-insensitive and run on both the raw text and a de-obfuscated copy.
/// </summary>
public static class PreFilter
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // PowerShell accepts '-' or '/' before a parameter (Unicode dashes are normalized to '-').
    private const string P = @"(?:^|[\s'"",])[-/]";

    // Any one of these makes the text "command-like".
    private static readonly (string Name, Regex Pattern)[] Gates =
    {
        // Shells and launchers
        ("powershell",      new(@"\b(powershell|pwsh)(\.exe)?\b", Opts)),
        ("cmd",             new(@"\bcmd(\.exe)?\s+(/[a-z](:\w+)?\s+)*/[ckr]\b", Opts)),
        ("mshta",           new(@"\bmshta(\.exe)?\b", Opts)),
        ("wscript",         new(@"\b(wscript|cscript)(\.exe)?\b", Opts)),
        ("conhost",         new(@"\bconhost(\.exe)?\b[^\r\n]*(--headless|\bcmd\b|\bpowershell\b)", Opts)),
        ("start-process",   new(@"\bstart-process\b", Opts)),

        // Download and execute
        ("iex",             new(@"\b(iex|invoke-expression)\b", Opts)),
        ("download",        new(@"\b(irm|iwr|invoke-restmethod|invoke-webrequest|downloadstring|downloadfile|downloaddata|net\.webclient|start-bitstransfer|winhttp\.winhttprequest|msxml2\.xmlhttp)\b", Opts)),
        ("curl-wget-url",   new(@"\b(curl|wget)(\.exe)?\b[^\r\n]*?\bhttps?://", Opts)),
        ("pipe-to-shell",   new(@"\|\s*(cmd|powershell|pwsh)(\.exe)?\b", Opts)),
        ("certutil",        new(@"\bcertutil(\.exe)?\b[^\r\n]*-(urlcache|decode|decodehex)", Opts)),
        ("bitsadmin",       new(@"\bbitsadmin(\.exe)?\b", Opts)),
        ("msiexec-url",     new(@"\bmsiexec(\.exe)?\b[^\r\n]*?(https?://|\\\\)", Opts)),
        ("finger",          new(@"\bfinger(\.exe)?\s+\S+@\S+", Opts)),
        ("net-use-remote",  new(@"\bnet\s+use\b[^\r\n]*(https?://|\\\\)", Opts)),
        ("webdav-unc",      new(@"\\\\[\w.\-]+@(ssl|\d+)\\", Opts)),
        ("unc-exec",        new(@"(\bstart\b|\bcall\b|&)\s*\\\\[\w.\-@]+\\", Opts)),

        // Living-off-the-land binaries
        ("rundll32",        new(@"\brundll32(\.exe)?\b", Opts)),
        ("regsvr32",        new(@"\bregsvr32(\.exe)?\b", Opts)),
        ("lolbin",          new(@"\b(syncappvpublishingserver|pcalua|forfiles|scriptrunner|cmstp|installutil|regasm|regsvcs|msbuild|odbcconf|ieexec)(\.exe|\.vbs)?\b", Opts)),

        // PowerShell switches attackers rely on
        ("encoded",         new(P + @"e[a-z]*['"",\s]+[A-Za-z0-9+/]{20,}={0,2}", Opts)),
        ("hidden-window",   new(P + @"w[a-z]*\s+['""]?(h(i(d(d(e(n)?)?)?)?)?|min[a-z]*|1|2)\b", Opts)),
        ("bypass",          new(P + @"(ep|ex|exec[a-z]*)['"",\s]+(bypass|unrestricted)\b", Opts)),

        // In-memory execution and decoding
        ("scriptblock",     new(@"\[scriptblock\]::create|\.addscript\s*\(|runspacefactory|createpipeline\s*\(", Opts)),
        ("frombase64",      new(@"frombase64string", Opts)),
        ("xor",             new(@"-bxor\b", Opts)),
        ("char-codes",      new(@"\[char\[\]\]|\[char\]\s*\(?\s*\d{2,3}\b|-join\s*\(?\s*@?\(\s*\d{2,3}\s*,\s*\d{2,3}", Opts)),
        ("crypto-decrypt",  new(@"cryptography\.(tripledes|aes|rijndael|des)\b|\.createdecryptor\s*\(", Opts)),
        ("add-type",        new(@"\badd-type\b[^\r\n]*-(typedefinition|memberdefinition)\b|getconsolewindow|showwindow\s*\(", Opts)),
        ("zone-bypass",     new(@"see_mask_nozonechecks", Opts)),

        // Obfuscation tricks themselves
        ("obfuscation",     new(@"(\^[a-z]){4,}|(`[a-z]){4,}", Opts)),
        ("quote-split",     new(@"[a-z]""[a-z0-9.]{0,6}""[a-z]", Opts)),
        ("format-operator", new(@"['""]\s*\{0\}\s*\{1\}[^'""]*['""]\s*-f\b", Opts)),
        ("call-string",     new(@"&\s*\(\s*['""][\w%.\-]+['""]\s*\)", Opts)),
        ("wildcard-call",   new(@"[&.]\s*\(\s*(gcm|get-command|gal|get-alias)\b[^)]*\*", Opts)),
        ("env-slice",       new(@"%\w+:~-?\d+|\$env:\w+\s*\[\s*\d+|%comspec%|\$env:comspec", Opts)),
        ("cmd-var-glue",    new(@"\bcmd\b[^\r\n]*/v:on\b|(\bset\s+\w+=[^&\r\n]*&\s*){3,}", Opts)),

        // Fake CAPTCHA wording placed in a comment after the command
        ("lure-comment",    new(@"(<#|#|\brem\b)[^\r\n]*\b(not a robot|i am human|i'm human|human verification|verification id|cloudflare id|ray id|challenge response|anti-bot|press\s+enter\s+to\s+verify)\b|<#\s*<?browser update", Opts)),
    };

    // Extra signals that are logged, but don't make text command-like on their own.
    private static readonly (string Name, Regex Pattern)[] Signals =
    {
        ("fake-verification", new(@"#[^\r\n]*\b(not a robot|i am human|i'm not a robot|verification|verify|captcha|cloudflare|ray id|human check)\b", Opts)),
        ("padding",           new(@"[ \t ]{20,}", Opts)),
        ("ip-url",            new(@"https?://\d{1,3}(\.(\d{1,3}|0x[0-9a-f]{1,2})){3}", Opts)),
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

    private static readonly Regex Invisible = new(@"[­​-‏⁠-⁤﻿]", Opts);
    private static readonly Regex Dashes = new(@"[‐-―−﹘﹣－]", Opts);
    private static readonly Regex SingleQuotes = new(@"[‘’‚‛]", Opts);
    private static readonly Regex DoubleQuotes = new(@"[“”„‟]", Opts);
    private static readonly Regex Concat = new(@"['""]\s*\+\s*['""]", Opts);
    private static readonly Regex EmptyQuotes = new(@"""""|''", Opts);
    private static readonly Regex SplitWord = new(@"(?<=[a-z])""([a-z0-9.]{0,6})""(?=[a-z])", Opts);
    private static readonly Regex UrlSlashes = new(@"\b(https?):[/\\]{2}", Opts);

    /// <summary>
    /// Undoes common tricks so the patterns see the real command:
    /// fullwidth letters, invisible characters, Unicode dashes and quotes (PowerShell accepts them),
    /// cmd escapes (p^o^w^e^r), PowerShell backticks, empty quotes (p""owershell),
    /// quote-split words (c"d"n), string concatenation ('pow'+'ershell') and backslashed URLs (http:/\).
    /// </summary>
    internal static string Deobfuscate(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        s = Invisible.Replace(s, "");
        s = Dashes.Replace(s, "-");
        s = SingleQuotes.Replace(s, "'");
        s = DoubleQuotes.Replace(s, "\"");
        s = s.Replace("^", "").Replace("`", "");
        s = Concat.Replace(s, "");
        s = EmptyQuotes.Replace(s, "");
        s = SplitWord.Replace(s, "$1");
        s = UrlSlashes.Replace(s, "$1://");
        return s;
    }
}

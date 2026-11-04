using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PasteGuard;
using Xunit;

namespace PasteGuard.Tests;

public class UpdaterTests
{
    private const string Download = "https://github.com/Ryankapa/PasteGuard/releases/download/";

    /// <summary>Answers by URL; records every URL asked for.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Code, byte[] Body)> _routes = new();
        public List<string> Requested { get; } = new();

        public FakeHandler Add(string url, string body, HttpStatusCode code = HttpStatusCode.OK) => Add(url, System.Text.Encoding.UTF8.GetBytes(body), code);

        public FakeHandler Add(string url, byte[] body, HttpStatusCode code = HttpStatusCode.OK)
        {
            _routes[url] = (code, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (code, body) = _routes.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, Array.Empty<byte>());
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
        }
    }

    private const string ApiUrl = "https://api.github.com/repos/Ryankapa/PasteGuard/releases/latest";

    private static string ReleaseJson(string tag, long size = 1234, string? exeUrl = null, string? hashUrl = null, bool withAssets = true) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = $"https://github.com/Ryankapa/PasteGuard/releases/tag/{tag}",
            body = "### Added\n- **Check for updates** in the tray",
            assets = withAssets
                ? new object[]
                {
                    new { name = "PasteGuard.exe", size, browser_download_url = exeUrl ?? $"{Download}{tag}/PasteGuard.exe" },
                    new { name = "PasteGuard.exe.sha256", size = 80, browser_download_url = hashUrl ?? $"{Download}{tag}/PasteGuard.exe.sha256" },
                }
                : Array.Empty<object>(),
        });

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData(" v10.20.30 ", "10.20.30")]
    public void Parses_version_tags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));

    [Theory]
    [InlineData("v2.0.0-beta")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("")]
    public void Rejects_other_tags(string tag) => Assert.Null(UpdateChecker.ParseVersion(tag));

    [Fact]
    public void Normalizes_versions_to_three_parts()
    {
        Assert.Equal(new Version(1, 2, 0), AppInfo.Normalize(new Version(1, 2)));
        Assert.Equal(new Version(1, 2, 3), AppInfo.Normalize(new Version(1, 2, 3, 4)));
        Assert.Equal(3, AppInfo.VersionText.Split('.').Length);
    }

    [Fact]
    public async Task Finds_a_newer_release()
    {
        var handler = new FakeHandler().Add(ApiUrl, ReleaseJson("v1.1.0"));
        using var checker = new UpdateChecker(handler);
        var update = await checker.CheckAsync(new Version(1, 0, 0));
        Assert.NotNull(update);
        Assert.Equal(new Version(1, 1, 0), update!.Version);
        Assert.Equal($"{Download}v1.1.0/PasteGuard.exe", update.ExeUrl);
        Assert.Equal($"{Download}v1.1.0/PasteGuard.exe.sha256", update.HashUrl);
        Assert.Equal(1234, update.ExeSize);
        Assert.Contains("Check for updates", update.Notes);
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    public async Task Reports_up_to_date_for_same_or_older(string tag)
    {
        var handler = new FakeHandler().Add(ApiUrl, ReleaseJson(tag));
        using var checker = new UpdateChecker(handler);
        Assert.Null(await checker.CheckAsync(new Version(1, 1, 0)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "limiting")]
    [InlineData(HttpStatusCode.NotFound, "No releases")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Explains_GitHub_errors(HttpStatusCode code, string expected)
    {
        var handler = new FakeHandler().Add(ApiUrl, "{}", code);
        using var checker = new UpdateChecker(handler);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Release_without_files_is_not_ready() =>
        Assert.Contains("isn't ready", Assert.Throws<UpdateException>(() => UpdateChecker.ParseRelease(ReleaseJson("v1.1.0", withAssets: false))).Message);

    [Theory]
    [InlineData("https://evil.example/PasteGuard.exe")]
    [InlineData("https://github.com/someone-else/PasteGuard/releases/download/v1.1.0/PasteGuard.exe")]
    [InlineData("http://github.com/Ryankapa/PasteGuard/releases/download/v1.1.0/PasteGuard.exe")]
    public void Refuses_downloads_from_anywhere_else(string url) =>
        Assert.Throws<UpdateException>(() => UpdateChecker.ParseRelease(ReleaseJson("v1.1.0", exeUrl: url)));

    [Fact]
    public void Refuses_garbage() =>
        Assert.Throws<UpdateException>(() => UpdateChecker.ParseRelease("not json"));

    [Fact]
    public void Reads_hash_files()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateChecker.ParseHashFile($"{hash.ToUpperInvariant()}  PasteGuard.exe"));
        Assert.Throws<UpdateException>(() => UpdateChecker.ParseHashFile("nope  PasteGuard.exe"));
    }

    /// <summary>Real PasteGuard bytes (the app assembly carries the right product name and version).</summary>
    private static byte[] RealPasteGuardBytes() => File.ReadAllBytes(typeof(AppInfo).Assembly.Location);

    private static UpdateInfo Info(Version version, long size) => new(
        version, $"v{version.ToString(3)}", "", "https://github.com/Ryankapa/PasteGuard/releases",
        $"{Download}v{version.ToString(3)}/PasteGuard.exe", size, $"{Download}v{version.ToString(3)}/PasteGuard.exe.sha256");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PasteGuardTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Downloads_and_verifies_a_matching_file()
    {
        var bytes = RealPasteGuardBytes();
        var info = Info(AppInfo.Version, bytes.Length);
        var handler = new FakeHandler()
            .Add(info.HashUrl, $"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}  PasteGuard.exe")
            .Add(info.ExeUrl, bytes);
        using var checker = new UpdateChecker(handler);
        var dir = TempDir();
        var progress = new List<int>();

        var path = await checker.DownloadAsync(info, dir, new SyncProgress(progress.Add));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(100, progress.Last());
        Assert.Single(Directory.GetFiles(dir)); // no leftover .download file
    }

    [Fact]
    public async Task Deletes_a_download_whose_hash_does_not_match()
    {
        var bytes = RealPasteGuardBytes();
        var info = Info(AppInfo.Version, bytes.Length);
        var handler = new FakeHandler()
            .Add(info.HashUrl, $"{new string('0', 64)}  PasteGuard.exe")
            .Add(info.ExeUrl, bytes);
        using var checker = new UpdateChecker(handler);
        var dir = TempDir();

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(info, dir));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Rejects_a_file_that_is_not_the_promised_version()
    {
        var bytes = RealPasteGuardBytes();
        var info = Info(new Version(99, 0, 0), bytes.Length); // the hash is right, but the file says it's another version
        var handler = new FakeHandler()
            .Add(info.HashUrl, Convert.ToHexString(SHA256.HashData(bytes)))
            .Add(info.ExeUrl, bytes);
        using var checker = new UpdateChecker(handler);
        var dir = TempDir();

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(info, dir));
        Assert.Contains("isn't PasteGuard 99.0.0", ex.Message);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Rejects_an_incomplete_download()
    {
        var bytes = RealPasteGuardBytes();
        var info = Info(AppInfo.Version, bytes.Length + 10);
        var handler = new FakeHandler()
            .Add(info.HashUrl, Convert.ToHexString(SHA256.HashData(bytes)))
            .Add(info.ExeUrl, bytes);
        using var checker = new UpdateChecker(handler);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(info, TempDir()));
        Assert.Contains("incomplete", ex.Message);
    }

    [Fact]
    public void Swaps_the_exe_and_can_revert()
    {
        var dir = TempDir();
        var current = Path.Combine(dir, "PasteGuard.exe");
        var downloaded = Path.Combine(dir, "download.exe");
        File.WriteAllText(current, "old version");
        File.WriteAllText(downloaded, "new version");

        UpdateInstaller.Swap(current, downloaded);
        Assert.Equal("new version", File.ReadAllText(current));
        Assert.Equal("old version", File.ReadAllText(current + ".old"));
        Assert.False(File.Exists(current + ".new"));

        UpdateInstaller.Revert(current);
        Assert.Equal("old version", File.ReadAllText(current));
        Assert.False(File.Exists(current + ".old"));
    }

    [Fact]
    public void Swap_works_while_the_old_exe_is_open()
    {
        var dir = TempDir();
        var current = Path.Combine(dir, "PasteGuard.exe");
        var downloaded = Path.Combine(dir, "download.exe");
        File.WriteAllText(current, "old version");
        File.WriteAllText(downloaded, "new version");

        // A running exe is open with delete sharing, which is what lets Windows rename it.
        using (new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            UpdateInstaller.Swap(current, downloaded);

        Assert.Equal("new version", File.ReadAllText(current));
    }

    private static (string current, string downloaded) InstallFiles()
    {
        var dir = TempDir();
        var current = Path.Combine(dir, "PasteGuard.exe");
        var downloaded = Path.Combine(dir, "updates-PasteGuard-9.9.9.exe");
        File.WriteAllText(current, "old version");
        File.WriteAllText(downloaded, "new version");
        return (current, downloaded);
    }

    [Fact]
    public void Install_replaces_the_old_exe_and_starts_it_as_updated()
    {
        var (current, downloaded) = InstallFiles();
        var started = new List<(string, string?)>();

        var error = UpdateInstaller.Install(downloaded, current, "1.1.0", (exe, from) => started.Add((exe, from)), delay: TimeSpan.Zero);

        Assert.Null(error);
        Assert.Equal("new version", File.ReadAllText(current));
        Assert.Equal(new[] { (current, (string?)"1.1.0") }, started);
    }

    [Fact]
    public void Install_waits_for_the_old_exe_to_be_released()
    {
        var (current, downloaded) = InstallFiles();
        var started = 0;

        // The old version still has its exe open (no delete sharing) for the first attempts.
        var locked = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
        string? error;
        using (var timer = new System.Threading.Timer(_ => locked.Dispose(), null, 150, Timeout.Infinite))
            error = UpdateInstaller.Install(downloaded, current, "1.1.0", (_, _) => started++,
                attempts: 50, delay: TimeSpan.FromMilliseconds(20));

        Assert.Null(error);
        Assert.Equal(1, started);
        Assert.Equal("new version", File.ReadAllText(current));
    }

    [Fact]
    public void Install_puts_the_old_version_back_when_the_new_one_wont_start()
    {
        var (current, downloaded) = InstallFiles();
        var started = new List<(string, string?)>();

        var error = UpdateInstaller.Install(downloaded, current, "1.1.0", (exe, from) =>
        {
            started.Add((exe, from));
            if (from is not null) throw new System.ComponentModel.Win32Exception(5);
        }, delay: TimeSpan.Zero);

        Assert.NotNull(error);
        Assert.Contains("(error 5)", error);
        Assert.Equal("old version", File.ReadAllText(current));
        Assert.False(File.Exists(current + ".old"));
        // Tried the new version, then restarted the old one so protection stays on.
        Assert.Equal(new[] { (current, (string?)"1.1.0"), (current, (string?)null) }, started);
    }

    [Fact]
    public void Install_refuses_a_target_that_isnt_an_exe()
    {
        var (_, downloaded) = InstallFiles();
        var started = 0;
        var error = UpdateInstaller.Install(downloaded, Path.Combine(TempDir(), "notes.txt"), "1.1.0", (_, _) => started++);
        Assert.NotNull(error);
        Assert.Equal(0, started);
    }

    [Fact]
    public void Install_refuses_a_missing_download_and_leaves_the_current_exe()
    {
        var (current, _) = InstallFiles();
        var before = File.ReadAllText(current);
        var started = 0;
        var error = UpdateInstaller.Install(Path.Combine(TempDir(), "gone.exe"), current, "1.1.0", (_, _) => started++);
        Assert.NotNull(error);
        Assert.Equal(0, started);
        Assert.Equal(before, File.ReadAllText(current));
    }

    [Fact]
    public async Task Cleanup_removes_leftovers()
    {
        var dir = TempDir();
        var current = Path.Combine(dir, "PasteGuard.exe");
        File.WriteAllText(current, "x");
        File.WriteAllText(current + ".old", "x");
        File.WriteAllText(current + ".new", "x");

        await UpdateInstaller.CleanupAsync(current, justUpdated: false);

        Assert.True(File.Exists(current));
        Assert.False(File.Exists(current + ".old"));
        Assert.False(File.Exists(current + ".new"));
    }

    [Fact]
    public void Formats_release_notes_as_plain_text() =>
        Assert.Equal("Added\r\n• Check for updates in the tray (see docs)",
            UpdateForm.FormatNotes("### Added\n- **Check for updates** in the `tray` (see [docs](https://example.com))"));

    /// <summary>
    /// Against the real GitHub release. Off by default (needs internet and downloads ~70 MB);
    /// run with PASTEGUARD_ONLINE_TESTS=1.
    /// </summary>
    [Fact]
    public async Task Online_latest_release_downloads_and_verifies()
    {
        if (Environment.GetEnvironmentVariable("PASTEGUARD_ONLINE_TESTS") != "1") return;

        using var checker = new UpdateChecker();
        var update = await checker.CheckAsync(new Version(0, 0, 1));
        Assert.NotNull(update);
        var path = await checker.DownloadAsync(update!, TempDir());
        Assert.True(new FileInfo(path).Length > 1_000_000);
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly Action<int> _report;
        public SyncProgress(Action<int> report) => _report = report;
        public void Report(int value) => _report(value);
    }
}

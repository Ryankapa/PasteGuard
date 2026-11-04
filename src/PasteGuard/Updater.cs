using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PasteGuard;

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string ExeUrl, long ExeSize, string HashUrl)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>
/// Checks GitHub Releases for a newer PasteGuard and downloads it. Only talks to github.com
/// (and GitHub's download servers it redirects to), and only when the user asks.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string ExeName = "PasteGuard.exe";
    public const string HashName = "PasteGuard.exe.sha256";
    private const long MaxExeBytes = 400L * 1024 * 1024;
    private static readonly string AssetPrefix = $"{AppInfo.RepoUrl}/releases/download/";

    private readonly HttpClient _http;
    private readonly string _apiUrl;

    public UpdateChecker(HttpMessageHandler? handler = null, string apiBase = "https://api.github.com")
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-call timeouts below; downloads can be slow
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        _apiUrl = $"{apiBase.TrimEnd('/')}/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
    }

    /// <summary>The latest release if it's newer than <paramref name="current"/>, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, _apiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        string json;
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token);
            json = await response.Content.ReadAsStringAsync(timeout.Token);
            if ((int)response.StatusCode == 404) throw new UpdateException("No releases were found on GitHub.");
            if ((int)response.StatusCode is 403 or 429) throw new UpdateException("GitHub is limiting requests right now. Try again in an hour.");
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub returned an error ({(int)response.StatusCode}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("GitHub didn't answer in time. Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Couldn't reach GitHub. Check your internet connection.", ex);
        }

        var latest = ParseRelease(json);
        return latest.Version > AppInfo.Normalize(current) ? latest : null;
    }

    internal static UpdateInfo ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag) ?? throw new UpdateException($"The latest release has an unexpected version tag ({tag}).");
            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            var page = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? AppInfo.ReleasesUrl : AppInfo.ReleasesUrl;

            string? exeUrl = null, hashUrl = null;
            long exeSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (name == ExeName) { exeUrl = url; exeSize = asset.GetProperty("size").GetInt64(); }
                    else if (name == HashName) hashUrl = url;
                }
            }

            if (exeUrl is null || hashUrl is null)
                throw new UpdateException($"Version {version.ToString(3)} is out, but its download isn't ready yet. Try again in a few minutes.");
            if (!IsTrustedAssetUrl(exeUrl) || !IsTrustedAssetUrl(hashUrl))
                throw new UpdateException("The release's download link doesn't point to PasteGuard's GitHub page, so it was not used.");

            return new UpdateInfo(version, tag, notes, page, exeUrl, exeSize, hashUrl);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpdateException("GitHub sent back a release this version doesn't understand.", ex);
        }
    }

    /// <summary>"v1.2.3" or "1.2" → 1.2.3 / 1.2.0. Pre-release tags like "v2.0.0-beta" are not accepted.</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag.Trim(), @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    internal static bool IsTrustedAssetUrl(string url) =>
        url.StartsWith(AssetPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>Reads the hash out of a "&lt;sha256&gt;  PasteGuard.exe" file.</summary>
    internal static string ParseHashFile(string text)
    {
        var m = Regex.Match(text, @"\b([0-9a-fA-F]{64})\b");
        if (!m.Success) throw new UpdateException("The release's SHA-256 file is not in the expected format.");
        return m.Groups[1].Value.ToLowerInvariant();
    }

    /// <summary>
    /// Downloads the new exe, checks its size, SHA-256 hash and version, and returns its path.
    /// <paramref name="progress"/> gets 0–100.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, string folder, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string expectedHash;
        try
        {
            using var hashTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hashTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            expectedHash = ParseHashFile(await _http.GetStringAsync(update.HashUrl, hashTimeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("The download timed out. Try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Couldn't download the update. Check your internet connection.", ex);
        }

        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"PasteGuard-{update.VersionText}.exe");
        var partial = target + ".download";

        try
        {
            using (var response = await _http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException($"The download failed ({(int)response.StatusCode}).");
                var total = response.Content.Headers.ContentLength ?? update.ExeSize;
                if (total > MaxExeBytes) throw new UpdateException("The download is far bigger than expected, so it was stopped.");

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var lastPercent = -1;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        received += read;
                        if (received > MaxExeBytes) throw new UpdateException("The download is far bigger than expected, so it was stopped.");
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        var percent = total > 0 ? (int)Math.Min(100, received * 100 / total) : 0;
                        if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                    }
                    if (update.ExeSize > 0 && received != update.ExeSize)
                        throw new UpdateException("The download was incomplete. Try again.");
                }

                var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (actual != expectedHash)
                    throw new UpdateException("The downloaded file doesn't match the release's SHA-256 hash, so it was deleted and not installed.");
            }

            CheckFileVersion(partial, update.Version);
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Couldn't download the update. Check your internet connection.", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>The exe's own version stamp must be PasteGuard at the version the release claims.</summary>
    internal static void CheckFileVersion(string path, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var fileVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        if (info.ProductName != "PasteGuard" || fileVersion != expected)
            throw new UpdateException($"The downloaded file isn't PasteGuard {expected.ToString(3)}, so it was not installed.");
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Installs a downloaded version, restarts, and tidies up afterwards.
///
/// The running exe is never replaced while it runs: PasteGuard is a single-file app, and .NET reads parts of
/// the app from its exe file on demand. Swapping that file under a running copy makes those reads fail
/// (FileNotFoundException), which breaks starting the new version and anything else that opens a program
/// or link until restart. So the old version copies its own exe to the download folder, starts that copy
/// with <see cref="InstallToArg"/> and <see cref="InstallFromArg"/>, and exits. The copy waits for it to close,
/// puts the downloaded exe in place of the old one and starts it. Because the installer is the old version's
/// own code, this works with any new version, including ones that don't know about installing.
/// </summary>
public static class UpdateInstaller
{
    public const string UpdatedFromArg = "--updated-from";
    public const string InstallToArg = "--install-to";
    public const string InstallFromArg = "--install-from";
    public const string InstallerName = "PasteGuard-installer.exe";

    public static string DownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PasteGuard", "updates");

    /// <summary>The exe to replace, or null when running in a way that can't be updated (e.g. "dotnet run").</summary>
    public static string? CurrentExe
    {
        get
        {
            var path = Environment.ProcessPath;
            if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
    }

    /// <summary>
    /// Step 1, in the running old version: copy this exe next to the download and start the copy, which installs
    /// <paramref name="downloadedExe"/> over <paramref name="currentExe"/> once this instance has exited.
    /// Throws <see cref="UpdateException"/> if the copy can't be made; launch errors are passed through.
    /// </summary>
    public static void StartInstaller(string downloadedExe, string currentExe)
    {
        var installer = Path.Combine(Path.GetDirectoryName(downloadedExe)!, InstallerName);
        try
        {
            File.Copy(currentExe, installer, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"Couldn't prepare the update in {Path.GetDirectoryName(installer)}.", ex);
        }

        var psi = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        psi.ArgumentList.Add(InstallToArg);
        psi.ArgumentList.Add(currentExe);
        psi.ArgumentList.Add(InstallFromArg);
        psi.ArgumentList.Add(downloadedExe);
        psi.ArgumentList.Add(UpdatedFromArg);
        psi.ArgumentList.Add(AppInfo.VersionText);
        Process.Start(psi);
    }

    /// <summary>
    /// Step 2, in the installer copy (after the old version has exited): copy <paramref name="newExe"/> over
    /// <paramref name="targetExe"/> and start it. Retries for a while because the old exe can stay locked for a
    /// moment after it exits. If anything fails, the old exe is put back and started again.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? Install(string newExe, string targetExe, string updatedFrom,
        Action<string, string?>? launch = null, int attempts = 20, TimeSpan? delay = null)
    {
        launch ??= Launch;
        if (!Path.IsPathFullyQualified(targetExe) || !targetExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(targetExe))
            return $"The update was asked to replace a file that isn't a PasteGuard exe ({targetExe}).";
        if (!newExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(newExe))
            return $"The downloaded update is missing ({newExe}).";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Swap(targetExe, newExe);
                break;
            }
            catch (UpdateException ex) when (attempt < attempts)
            {
                Log.Write("UPDATE", $"Replacing {targetExe} failed (attempt {attempt} of {attempts}): {ex.InnerException?.Message ?? ex.Message}");
                Thread.Sleep(delay ?? TimeSpan.FromMilliseconds(500));
            }
            catch (UpdateException ex)
            {
                TryLaunch(launch, targetExe, null); // keep the user protected on the old version
                return ex.Message;
            }
        }

        try
        {
            launch(targetExe, updatedFrom);
            return null;
        }
        catch (Exception ex) when (IsLaunchError(ex))
        {
            Revert(targetExe);
            TryLaunch(launch, targetExe, null);
            return $"The new version couldn't be started, so the previous version was put back.\n\nWindows said: {Describe(ex)}";
        }
    }

    private static void TryLaunch(Action<string, string?> launch, string exe, string? updatedFrom)
    {
        try { launch(exe, updatedFrom); } catch (Exception ex) when (IsLaunchError(ex)) { }
    }

    /// <summary>
    /// Puts <paramref name="newExe"/> in place of <paramref name="currentExe"/> (which must not be running),
    /// keeping the original as "&lt;name&gt;.old" until the new version has started and cleans it up.
    /// On failure the original file is put back.
    /// </summary>
    public static void Swap(string currentExe, string newExe)
    {
        var staged = currentExe + ".new";
        var old = currentExe + ".old";
        try
        {
            File.Copy(newExe, staged, overwrite: true); // same folder, so the renames below can't fail halfway across drives
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateChecker.TryDelete(staged);
            throw new UpdateException(
                $"PasteGuard can't write to its folder ({Path.GetDirectoryName(currentExe)}). " +
                $"The new version was saved to {newExe}. Replace PasteGuard.exe with it yourself.", ex);
        }

        UpdateChecker.TryDelete(old);
        try
        {
            File.Move(currentExe, old, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateChecker.TryDelete(staged);
            throw new UpdateException($"Couldn't replace PasteGuard.exe. The new version was saved to {newExe}.", ex);
        }

        try
        {
            File.Move(staged, currentExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(old, currentExe, overwrite: true);
            UpdateChecker.TryDelete(staged);
            throw new UpdateException($"Couldn't replace PasteGuard.exe. The new version was saved to {newExe}.", ex);
        }
    }

    /// <summary>Undoes <see cref="Swap"/> when the new version couldn't be started.</summary>
    public static void Revert(string currentExe)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Move(old, currentExe, overwrite: true);
    }

    /// <summary>Starts <paramref name="exe"/>; with <paramref name="updatedFrom"/> it shows the "updated" message.</summary>
    public static void Launch(string exe, string? updatedFrom)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (updatedFrom is not null)
        {
            psi.ArgumentList.Add(UpdatedFromArg);
            psi.ArgumentList.Add(updatedFrom);
        }
        Process.Start(psi);
    }

    public static bool IsLaunchError(Exception ex) =>
        ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException;

    /// <summary>Windows' own reason, e.g. "Access is denied. (error 5)".</summary>
    public static string Describe(Exception ex) =>
        ex is System.ComponentModel.Win32Exception w ? $"{w.Message.TrimEnd('.')}. (error {w.NativeErrorCode})" : ex.Message;

    /// <summary>
    /// Deletes leftovers from an earlier update: the old exe and, right after an update, the downloaded files.
    /// (Downloads are otherwise kept, because a failed install tells the user where the new exe was saved.)
    /// </summary>
    public static async Task CleanupAsync(string? currentExe, bool justUpdated)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var done = true;
            if (currentExe is not null)
            {
                foreach (var leftover in new[] { currentExe + ".old", currentExe + ".new" })
                {
                    UpdateChecker.TryDelete(leftover);
                    done &= !File.Exists(leftover);
                }
            }
            if (justUpdated)
            {
                try { if (Directory.Exists(DownloadFolder)) Directory.Delete(DownloadFolder, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { done = false; }
            }

            if (done) return;
            await Task.Delay(1000); // the previous version may still be closing
        }
    }
}

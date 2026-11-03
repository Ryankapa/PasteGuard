namespace PasteGuard;

/// <summary>Version and project details. The version is set once, in Directory.Build.props.</summary>
public static class AppInfo
{
    public const string RepoOwner = "Ryankapa";
    public const string RepoName = "PasteGuard";
    public const string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";
    public const string ReleasesUrl = RepoUrl + "/releases/latest";

    /// <summary>This build's version, always three parts (1.2.3).</summary>
    public static Version Version { get; } = Normalize(typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string VersionText => Version.ToString(3);

    public static string UserAgent => $"PasteGuard/{VersionText} (+{RepoUrl})";

    /// <summary>Drops the fourth (revision) part and fills missing parts with 0.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}

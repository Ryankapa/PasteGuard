using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasteGuard;

public enum ApiErrorAction
{
    /// <summary>Put the copied text back. Copy/paste keeps working if Jev is down.</summary>
    Restore,
    /// <summary>Keep command-like text blocked when it could not be checked.</summary>
    Block,
}

public sealed class Settings
{
    public const int DefaultThreshold = 70;
    public const int MinThreshold = 1;
    public const int MaxThreshold = 100;

    // Kept from the pre-rename build so keys saved earlier still decrypt.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClickFixBlocker.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The Jev API key, encrypted with Windows DPAPI for the current user.</summary>
    public string? ApiKeyProtected { get; set; }
    public int Threshold { get; set; } = DefaultThreshold;
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public ApiErrorAction OnApiError { get; set; } = ApiErrorAction.Restore;
    public string Model { get; set; } = "jev-latest";
    public string ApiBaseUrl { get; set; } = "https://api.typesafe.ai";

    /// <summary>SHA-256 fingerprints of exact commands the user chose to always allow.</summary>
    public List<string> AllowedCommands { get; set; } = new();

    public bool IsAllowed(string text) => AllowedCommands.Contains(Fingerprint(text));

    public void Allow(string text)
    {
        var fp = Fingerprint(text);
        if (!AllowedCommands.Contains(fp)) AllowedCommands.Add(fp);
    }

    /// <summary>Same command → same fingerprint, ignoring surrounding whitespace and padding.</summary>
    internal static string Fingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TextPrep.ForAnalysis(text)))).ToLowerInvariant();

    [JsonIgnore]
    public string ApiKey
    {
        get => Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = Protect(value);
    }

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PasteGuard", "config.json");

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static Settings Load(string? path = null)
    {
        path ??= DefaultPath;
        Settings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings()
                : new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Write("ERROR", $"Could not read settings, using defaults: {ex.Message}");
            settings = new Settings();
        }

        settings.Threshold = Math.Clamp(settings.Threshold, MinThreshold, MaxThreshold);
        settings.AllowedCommands ??= new();
        if (string.IsNullOrWhiteSpace(settings.Model)) settings.Model = "jev-latest";
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl)) settings.ApiBaseUrl = "https://api.typesafe.ai";
        return settings;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    internal static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    internal static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted for another Windows account or corrupted: treat as "no key".
            return "";
        }
    }
}

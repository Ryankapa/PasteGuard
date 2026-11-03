namespace PasteGuard;

/// <summary>Small local log at %LOCALAPPDATA%\PasteGuard\events.log. Never contains the API key.</summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static string PathOnDisk { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PasteGuard", "events.log");

    /// <summary>All entries from the current and previous log file, oldest first.</summary>
    public static List<LogEntry> ReadEntries()
    {
        var entries = new List<LogEntry>();
        lock (Gate)
        {
            foreach (var path in new[] { PathOnDisk + ".old", PathOnDisk })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                        if (LogEntry.TryParse(line, out var entry)) entries.Add(entry);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return entries;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { File.Delete(PathOnDisk + ".old"); } catch { }
            try { File.Delete(PathOnDisk); } catch { }
        }
    }

    public static void Write(string kind, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
                var info = new FileInfo(PathOnDisk);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(PathOnDisk, PathOnDisk + ".old", overwrite: true);

                File.AppendAllText(PathOnDisk, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {kind,-16} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the agent.
        }
    }
}

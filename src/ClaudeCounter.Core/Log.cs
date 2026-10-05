using System.Text;

namespace ClaudeCounter.Core;

/// <summary>
/// Minimal append-only file logger. One timestamped line per event, written to
/// %LOCALAPPDATA%\ClaudeCounter\logs\claudecounter.log. Rolls to a single .1
/// backup once the file passes ~1 MB so it never grows unbounded. Every failure
/// is swallowed: logging must never take down the tray app.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;

    private static readonly object Gate = new();
    private static readonly string? LogPath = BuildPath();

    public static string? FilePath => LogPath;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static string? BuildPath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeCounter", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "claudecounter.log");
        }
        catch
        {
            return null;
        }
    }

    private static void Write(string level, string message)
    {
        if (LogPath is null)
            return;
        try
        {
            lock (Gate)
            {
                RollIfNeeded();
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath!);
            if (!info.Exists || info.Length <= MaxBytes)
                return;
            var backup = LogPath + ".1";
            File.Delete(backup); // no-op when absent
            File.Move(LogPath!, backup);
        }
        catch
        {
            // A roll failure is non-fatal; keep appending to the current file.
        }
    }
}

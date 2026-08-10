using System.Runtime.CompilerServices;
using System.Text;

// Lets LogTests exercise WithRetry directly with a fake failing action,
// instead of taking a real OS-level exclusive lock on the shared log file
// this whole test assembly writes to - doing that would make this test
// itself the concurrent-writer hazard I4 exists to fix, and would make every
// other test that touches Log.FilePath flaky whenever it happens to run in
// parallel with this one.
[assembly: InternalsVisibleTo("ClaudeCounter.Tests")]

// RelativePathGuard and CredentialScrubber moved here from ClaudeBackup.csproj
// (see their doc comments) so the restore engine, also in this assembly, can
// reuse them. GitBackend and RcloneBackend - still in ClaudeBackup.csproj -
// call both as internal members, exactly as before the move; this is what
// keeps that access working across the assembly boundary.
[assembly: InternalsVisibleTo("ClaudeBackup")]

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

    // How many times a write/roll retries after a sharing violation before
    // giving up. This file is written by both the tray and the worker (see
    // class doc), which was not true before this release - previously only
    // one process ever wrote it. AppendAllText opens with FileShare.Read, so
    // a second concurrent writer gets an IOException; the in-process `lock
    // (Gate)` above does nothing to prevent that, since it only serializes
    // writers within THIS process. Left unhandled, that exception falls into
    // the catch below and the line is silently dropped - unacceptable here
    // because the log is the ONLY diagnostic channel ("see the log for
    // details" appears in every failure message this app shows). A few
    // retries with a short backoff gives the other process's write - which
    // takes microseconds - time to finish and release its handle.
    private const int MaxRetries = 5;
    private const int RetryDelayMs = 15;

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
                WithRetry(() => File.AppendAllText(LogPath, line, Encoding.UTF8));
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
            WithRetry(() =>
            {
                File.Delete(backup); // no-op when absent
                File.Move(LogPath!, backup);
            });
        }
        catch
        {
            // A roll failure is non-fatal; keep appending to the current file.
        }
    }

    /// <summary>
    /// Retries <paramref name="action"/> up to <see cref="MaxRetries"/> times
    /// with a short fixed backoff, swallowing only <see cref="IOException"/>
    /// between attempts (a sharing violation from a concurrent writer - the
    /// case this exists for) - any other exception, or the final attempt's
    /// IOException, propagates to the caller's own catch, which is what keeps
    /// this method non-throwing overall via Write's and RollIfNeeded's own
    /// try/catch. Not a general-purpose retry helper: it exists solely to
    /// close the concurrent-writer gap described above.
    /// </summary>
    internal static void WithRetry(Action action)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }
}

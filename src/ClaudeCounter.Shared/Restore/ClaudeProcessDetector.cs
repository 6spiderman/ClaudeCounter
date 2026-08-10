using System.Diagnostics;

namespace ClaudeBackup;

/// <summary>
/// Restore safety rule 6: detect whether Claude Code appears to be running,
/// so the (future) restore dialog can warn before applying. Never a hard
/// block - the user may know better than a name-based heuristic - so this is
/// exposed as a flag for a caller to act on, not a gate this class enforces
/// itself.
///
/// Detection is a plain process-name check: on a real machine with an active
/// session, the Claude Code CLI runs as "claude.exe" (confirmed via a live
/// process list and via ~/.claude/daemon.lock's own "launchTarget" field,
/// which names ...\claude-code\bin\claude.exe), so any running process named
/// "claude" counts as "appears to be running". This is a heuristic, not a
/// guarantee: a differently-named build, a process that exits between this
/// check and the actual restore, or a machine where something unrelated
/// happens to be named "claude" can all make it wrong in either direction -
/// which is exactly why restore rule 6 says "warn", not "block".
/// </summary>
public static class ClaudeProcessDetector
{
    private const string ProcessName = "claude";

    /// <summary>Real check against the OS process list.</summary>
    public static bool AppearsRunning() => AppearsRunning(RealRunningCount);

    /// <summary>
    /// Testable overload: <paramref name="runningProcessCount"/> stands in
    /// for "how many processes named 'claude' are currently running", so
    /// this can be exercised without spawning or inspecting a real process.
    /// Any exception the count function throws (e.g. a permissions problem
    /// enumerating the process list) is treated as "not detected" rather than
    /// propagated - this feeds an informational warning, not a safety gate,
    /// so a detection failure must never block or crash a restore.
    /// </summary>
    internal static bool AppearsRunning(Func<int> runningProcessCount)
    {
        try
        {
            return runningProcessCount() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static int RealRunningCount()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }
}

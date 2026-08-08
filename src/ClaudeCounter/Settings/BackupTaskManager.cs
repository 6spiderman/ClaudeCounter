using System.Diagnostics;
using System.Globalization;
using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.Settings;

/// <summary>
/// Bridges the tray app to the standalone ClaudeBackup.exe worker: detects
/// whether it is installed alongside the tray exe, launches it on demand, and
/// registers/unregisters a per-user Windows Task Scheduler entry that runs it
/// on a schedule. Never runs the worker in-process, and this project carries
/// no ProjectReference to ClaudeBackup.csproj at all - detection is a plain
/// File.Exists check. The config types (ClaudeBackup.BackupConfig /
/// ScheduleConfig / GitTarget / DriveTarget) used below live in
/// ClaudeCounter.Shared, which both this project and the worker reference; do
/// not re-add a reference to ClaudeBackup.csproj to "simplify" this - that
/// drags the worker's own RID-specific publish graph into the tray's
/// single-file publish and breaks it (see the release workflow's publish
/// smoke test).
/// </summary>
public static class BackupTaskManager
{
    public const string TaskName = "ClaudeCounter Backup";

    /// <summary>
    /// The path to ClaudeBackup.exe next to the running tray exe, or null when
    /// it is not installed (e.g. a portable zip that omitted it, or an older
    /// install that predates the backup feature).
    /// </summary>
    public static string? WorkerPath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, "ClaudeBackup.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>True when the backup worker is installed and the feature should be shown.</summary>
    public static bool WorkerAvailable() => WorkerPath() is not null;

    /// <summary>Launches the worker as a detached child process with no console window.</summary>
    public static void RunNow()
    {
        if (WorkerPath() is not { } path)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            Log.Warn($"Could not launch the backup worker: {e.Message}");
        }
    }

    /// <summary>
    /// Builds the schtasks.exe command-line argument string for registering
    /// the backup schedule. Pure and unit-testable - performs no I/O.
    /// </summary>
    /// <remarks>
    /// The whole /TR value is wrapped in an outer pair of quotes (so a space in
    /// <paramref name="workerPath"/> does not split it into two schtasks
    /// arguments) with an escaped inner pair around the path itself (so
    /// schtasks - and later Task Scheduler, when it runs the stored action -
    /// can tell where the executable path ends). This is the standard schtasks
    /// idiom for a /TR value that contains spaces.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="schedule"/>'s Time is not a strict 24-hour "HH:mm" value.
    /// Time reaches here as free text from a Settings textbox; without this
    /// check a value like <c>09:00" /TR "calc.exe</c> would splice extra
    /// arguments into the schtasks command line.
    /// </exception>
    public static string BuildSchtasksArgs(ScheduleConfig schedule, string workerPath)
    {
        if (!TimeOnly.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new ArgumentException(
                $"Schedule time '{schedule.Time}' is not a valid 24-hour HH:mm value.", nameof(schedule));

        var sc = schedule.Frequency.ToLowerInvariant() switch
        {
            "hourly" => "HOURLY",
            "weekly" => "WEEKLY",
            _ => "DAILY",
        };
        // /F overwrites an existing task of the same name instead of erroring.
        // No /RU or /S: this registers a per-user task and needs no elevation.
        return $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{workerPath}\\\"\" /SC {sc} /ST {schedule.Time}";
    }

    /// <summary>
    /// Registers (or replaces) the scheduled task. Never throws; returns false
    /// on any failure (worker absent, invalid schedule, or a non-zero
    /// schtasks exit), with the reason logged via Log.Warn.
    /// </summary>
    public static bool Register(ScheduleConfig schedule)
    {
        if (WorkerPath() is not { } path)
            return false;

        string args;
        try
        {
            args = BuildSchtasksArgs(schedule, path);
        }
        catch (ArgumentException e)
        {
            Log.Warn($"Refused to register the backup schedule: {e.Message}");
            return false;
        }
        return Run(args);
    }

    /// <summary>Removes the scheduled task, if any. Never throws; returns false on failure.</summary>
    public static bool Unregister() => Run($"/Delete /F /TN \"{TaskName}\"");

    private static bool Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            if (p is null)
                return false;
            if (p.ExitCode != 0)
            {
                Log.Warn($"schtasks exited {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"schtasks failed: {e.Message}");
            return false;
        }
    }
}

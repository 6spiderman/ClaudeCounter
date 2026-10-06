using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.Settings;

/// <summary>
/// Linux counterpart of the Windows build's BackupTaskManager: finds the
/// ClaudeBackup worker next to the tray binary, runs it on demand, and keeps
/// its schedule as a systemd user timer instead of a Task Scheduler entry.
/// Same public surface as the Windows class, so the backup UI calls it the
/// same way on both platforms.
/// </summary>
/// <remarks>
/// The units live in <c>~/.config/systemd/user/</c> (the user's own systemd
/// instance - no root, nothing system-wide). How the Windows schedule
/// options map:
/// <list type="bullet">
/// <item>Daily / weekly / hourly at a time - the timer's <c>OnCalendar=</c>.
/// Weekly recurs on today's weekday, as on Windows; hourly at the given
/// minute past every hour.</item>
/// <item>StartWhenAvailable - <c>Persistent=true</c>: a run missed while the
/// machine was off or asleep happens at the next opportunity.</item>
/// <item>DisallowStartIfOnBatteries - <c>ConditionACPower=true</c>.</item>
/// <item>RestartOnFailure - <c>Restart=on-failure</c> with the interval and
/// a start limit of the given count.</item>
/// <item>RunOnlyIfNetworkAvailable and StopIfGoingOnBatteries have no
/// reliable user-level systemd equivalent and are not applied; the Linux
/// Settings UI does not offer them.</item>
/// </list>
/// The service is skipped (not failed) once the worker binary is gone, via
/// <c>ConditionPathExists=</c>, so uninstalling the package cannot leave a
/// timer that fails every day.
/// </remarks>
public static class BackupTaskManager
{
    public const string UnitBaseName = "claudecounter-backup";
    public const string WorkerFileName = "ClaudeBackup";

    private static string UnitDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "systemd", "user");

    private static string ServicePath => Path.Combine(UnitDirectory, UnitBaseName + ".service");
    private static string TimerPath => Path.Combine(UnitDirectory, UnitBaseName + ".timer");

    /// <summary>
    /// The path to the ClaudeBackup worker next to the running tray binary, or
    /// null when it is not installed (a portable archive without it, or an
    /// older package that predates backup).
    /// </summary>
    public static string? WorkerPath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, WorkerFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>True when the backup worker is installed and the feature should be shown.</summary>
    public static bool WorkerAvailable() => WorkerPath() is not null;

    /// <summary>
    /// Runs the worker and waits for it without blocking the UI thread.
    /// Returns its exit code (0/1/2 - see BackupRunner), or null if it could
    /// not be started.
    /// </summary>
    public static async Task<int?> RunNowAsync()
    {
        if (WorkerPath() is not { } path)
            return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
            if (process is null)
                return null;
            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Could not launch the backup worker: {e.Message}");
            return null;
        }
    }

    /// <summary>Same wording as the Windows build. Pure.</summary>
    public static string ResultMessage(int? exitCode) => exitCode switch
    {
        0 => "Backup complete.",
        1 => "Backup not run - check your backup settings.",
        2 => "Backup failed - see the log.",
        _ => "Could not start the backup worker. See the log for details.",
    };

    /// <summary>
    /// The timer's <c>OnCalendar=</c> expression for a schedule. Pure;
    /// <paramref name="today"/> picks the weekday for "weekly". Throws
    /// <see cref="ArgumentException"/> for a time that is not HH:mm.
    /// </summary>
    public static string OnCalendarFor(ScheduleConfig schedule, DayOfWeek today)
    {
        var time = ParseTimeOrThrow(schedule);
        var hh = time.Hour.ToString("00", CultureInfo.InvariantCulture);
        var mm = time.Minute.ToString("00", CultureInfo.InvariantCulture);
        return schedule.Frequency.ToLowerInvariant() switch
        {
            "weekly" => $"{today.ToString()[..3]} *-*-* {hh}:{mm}:00",
            "hourly" => $"*-*-* *:{mm}:00",
            _ => $"*-*-* {hh}:{mm}:00",
        };
    }

    /// <summary>The service and timer unit files for a schedule. Pure, for tests and preview.</summary>
    public static (string Service, string Timer) BuildUnits(ScheduleConfig schedule, string workerPath, DayOfWeek today)
    {
        var onCalendar = OnCalendarFor(schedule, today);

        // systemd unescapes C-style sequences and splits on spaces in
        // ExecStart; quoting the path keeps one with spaces in one piece.
        // A path containing a quote or backslash is refused outright rather
        // than half-escaped.
        if (workerPath.IndexOfAny(['"', '\\', '\n', '\r']) >= 0)
            throw new ArgumentException("The worker path contains characters that cannot be used in a systemd unit.");

        var service = new StringBuilder()
            .AppendLine("# Written by ClaudeCounter (Settings -> Backup). Edits are overwritten.")
            .AppendLine("[Unit]")
            .AppendLine("Description=ClaudeCounter backup")
            .AppendLine($"ConditionPathExists={workerPath}");
        if (schedule.DisallowStartIfOnBatteries)
            service.AppendLine("ConditionACPower=true");
        if (schedule.RestartOnFailure)
        {
            var interval = Math.Max(1, schedule.RestartIntervalMinutes);
            var count = Math.Max(1, schedule.RestartCount);
            // The first attempt plus `count` restarts, inside a window wide
            // enough to contain them.
            service.AppendLine($"StartLimitIntervalSec={(count + 1) * interval * 60 + 60}");
            service.AppendLine($"StartLimitBurst={count + 1}");
        }
        service
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=oneshot")
            .AppendLine($"ExecStart=\"{workerPath}\"")
            // Exit 1 means "nothing to do / not configured" (see BackupRunner),
            // not a failure worth restarting for.
            .AppendLine("SuccessExitStatus=1")
            .AppendLine("Nice=10");
        if (schedule.RestartOnFailure)
        {
            service.AppendLine("Restart=on-failure");
            service.AppendLine($"RestartSec={Math.Max(1, schedule.RestartIntervalMinutes)}min");
        }

        var timer = new StringBuilder()
            .AppendLine("# Written by ClaudeCounter (Settings -> Backup). Edits are overwritten.")
            .AppendLine("[Unit]")
            .AppendLine("Description=ClaudeCounter backup schedule")
            .AppendLine()
            .AppendLine("[Timer]")
            .AppendLine($"OnCalendar={onCalendar}")
            .AppendLine($"Persistent={(schedule.StartWhenAvailable ? "true" : "false")}")
            .AppendLine($"Unit={UnitBaseName}.service")
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=timers.target");

        return (service.ToString(), timer.ToString());
    }

    /// <summary>
    /// Writes the units and enables the timer. False (and logged) when the
    /// worker is missing, the schedule is invalid, or systemctl fails.
    /// </summary>
    public static bool Register(ScheduleConfig schedule)
    {
        if (WorkerPath() is not { } path)
            return false;

        string service, timer;
        try
        {
            (service, timer) = BuildUnits(schedule, path, DateTime.Now.DayOfWeek);
        }
        catch (ArgumentException e)
        {
            Log.Warn($"Refused to register the backup schedule: {e.Message}");
            return false;
        }

        try
        {
            Directory.CreateDirectory(UnitDirectory);
            File.WriteAllText(ServicePath, service);
            File.WriteAllText(TimerPath, timer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not write the backup schedule units: {e.Message}");
            return false;
        }

        // restart (not just enable --now) so an edited schedule replaces the
        // one an already-running timer had loaded.
        return Systemctl("daemon-reload")
            && Systemctl("enable", UnitBaseName + ".timer")
            && Systemctl("restart", UnitBaseName + ".timer");
    }

    /// <summary>Stops and removes the timer. Never throws.</summary>
    public static UnregisterOutcome Unregister()
    {
        if (!File.Exists(TimerPath) && !File.Exists(ServicePath))
            return UnregisterOutcome.NotFound;

        // Best-effort disable first; a timer that was never enabled is fine.
        Systemctl("disable", "--now", UnitBaseName + ".timer");
        try
        {
            File.Delete(TimerPath);
            File.Delete(ServicePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not remove the backup schedule units: {e.Message}");
            return UnregisterOutcome.Failed;
        }
        return Systemctl("daemon-reload") ? UnregisterOutcome.Removed : UnregisterOutcome.Failed;
    }

    private static TimeOnly ParseTimeOrThrow(ScheduleConfig schedule)
    {
        if (!TimeOnly.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException($"Schedule time '{schedule.Time}' is not HH:mm.");
        return time;
    }

    private static bool Systemctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--user");
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return false;
            // Drain both pipes before waiting, so a chatty stderr cannot block it.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                Log.Warn($"systemctl --user {string.Join(' ', args)} timed out.");
                return false;
            }
            if (process.ExitCode != 0)
            {
                Log.Warn($"systemctl --user {string.Join(' ', args)} exited {process.ExitCode}: {stderr.GetAwaiter().GetResult().Trim()}");
                return false;
            }
            _ = stdout.GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            Log.Warn($"Could not run systemctl: {e.Message}");
            return false;
        }
    }
}

/// <summary>Outcome of BackupTaskManager.Unregister() - same meaning as on Windows.</summary>
public enum UnregisterOutcome
{
    /// <summary>A schedule existed and was removed.</summary>
    Removed,

    /// <summary>No schedule existed - nothing needed to happen. Not an error.</summary>
    NotFound,

    /// <summary>A schedule existed and removing it failed.</summary>
    Failed,
}

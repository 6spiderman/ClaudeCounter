using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
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
/// ClaudeCounter.Core, which both this project and the worker reference; do
/// not re-add a reference to ClaudeBackup.csproj to "simplify" this - that
/// drags the worker's own RID-specific publish graph into the tray's
/// single-file publish and breaks it (see the release workflow's publish
/// smoke test).
/// </summary>
public static class BackupTaskManager
{
    // M2: also hardcoded, separately, in packaging/inno/ClaudeCounter.iss's
    // CurUninstallStepChanged (the uninstaller cannot import a C# const) -
    // keep both in sync. Renaming this without updating the .iss would
    // orphan the scheduled task of every already-installed copy: uninstall
    // would delete a task by the OLD name while this const would only ever
    // register the NEW one.
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

    /// <summary>
    /// Launches the worker and asynchronously waits for it to exit, off the
    /// UI thread (the `await` yields back to the message loop while the
    /// process runs, so this is safe to call directly from a button/menu
    /// click handler without freezing the UI). Returns the worker's exit code
    /// (0/1/2 - see BackupRunner's doc comment), or null if the worker could
    /// not be started at all (not installed, or Process.Start/launch threw).
    /// Previously this was fire-and-forget: the exit code the worker was
    /// built around was never read, so a failing "back up now" looked
    /// identical to a successful one. Pair with <see cref="ResultMessage"/>
    /// to turn the result into UI text.
    /// </summary>
    public static async Task<int?> RunNowAsync()
    {
        if (WorkerPath() is not { } path)
            return null;
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
            if (process is null)
                return null;
            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not launch the backup worker: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Maps a <see cref="RunNowAsync"/> result to the text a "back up now"
    /// caller should show the user. Pure and unit-testable - performs no I/O.
    /// </summary>
    public static string ResultMessage(int? exitCode) => exitCode switch
    {
        0 => "Backup complete.",
        1 => "Backup not run - check your backup settings.",
        2 => "Backup failed - see the log.",
        _ => "Could not start the backup worker. See the log for details.",
    };

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
        ParseTimeOrThrow(schedule);

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
    /// Shared HH:mm validation for both <see cref="BuildSchtasksArgs"/> (kept
    /// for the /Delete and /Query paths - see this class's summary) and <see
    /// cref="BuildTaskXml"/>, which is where registration's real logic now
    /// lives. Time reaches here as free text from a Settings textbox; without
    /// this check a value that is not strictly "HH:mm" could splice extra
    /// content into a generated schtasks command line or task XML.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="schedule"/>'s Time is not a strict 24-hour "HH:mm" value.
    /// </exception>
    private static TimeOnly ParseTimeOrThrow(ScheduleConfig schedule)
    {
        if (!TimeOnly.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            throw new ArgumentException(
                $"Schedule time '{schedule.Time}' is not a valid 24-hour HH:mm value.", nameof(schedule));
        return time;
    }

    // Task Scheduler's own XML namespace - every element in the document
    // must be qualified with this or schtasks /Create /XML rejects the file
    // as an invalid task definition.
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Builds the Task Scheduler XML definition for registering the backup
    /// schedule - the real logic behind <see cref="Register"/> now that
    /// registration switched from <c>schtasks /Create</c> with flags (which
    /// cannot express "run a missed backup as soon as possible" or the two
    /// battery settings) to <c>schtasks /Create /XML &lt;file&gt;</c>. Pure
    /// and unit-testable - performs no I/O, never touches the filesystem or
    /// invokes schtasks itself; <see cref="Register"/> writes the result to a
    /// temp file and passes that to schtasks.
    /// </summary>
    /// <remarks>
    /// Element order within a given parent does not matter to Task
    /// Scheduler's schema (its complex types use xs:all, not xs:sequence),
    /// but the order below follows what Task Scheduler itself emits when
    /// exporting a task, since that is a known-good reference rather than a
    /// guess. A caller (a test, in particular) should round-trip this
    /// through <see cref="XDocument"/> rather than string-matching -
    /// confirming well-formedness and the presence/value of each element is
    /// what actually proves the document is valid, not substring checks
    /// against a specific serialization.
    ///
    /// The &lt;Command&gt; element carries <paramref name="workerPath"/>
    /// verbatim - unlike schtasks' command-line /TR value, an XML element's
    /// text content does not need its own quoting for an embedded space;
    /// XElement's text-content escaping (for XML metacharacters like
    /// &amp;/&lt;/&gt;) is all that is required.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="schedule"/>'s Time is not a strict 24-hour "HH:mm" value.
    /// </exception>
    public static string BuildTaskXml(ScheduleConfig schedule, string workerPath)
    {
        var time = ParseTimeOrThrow(schedule);

        // Local wall-clock time, no UTC/offset suffix - Task Scheduler
        // treats an offset-less StartBoundary as local time, which is the
        // same "wall clock, whatever timezone the machine is in right now"
        // semantics /ST always had via schtasks.
        var startBoundary = (DateTime.Now.Date + time.ToTimeSpan())
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var trigger = schedule.Frequency.ToLowerInvariant() switch
        {
            // Weekly: recurs on the same day of the week as the start
            // boundary, matching what registering "weekly, starting today"
            // has always meant here - there is no separate day-of-week
            // field on ScheduleConfig to honour instead.
            "weekly" => new XElement(TaskNs + "CalendarTrigger",
                new XElement(TaskNs + "StartBoundary", startBoundary),
                new XElement(TaskNs + "Enabled", "true"),
                new XElement(TaskNs + "ScheduleByWeek",
                    new XElement(TaskNs + "DaysOfWeek",
                        new XElement(TaskNs + DateTime.Now.DayOfWeek.ToString())),
                    new XElement(TaskNs + "WeeksInterval", "1"))),
            // Hourly: a one-time trigger that repeats every hour forever
            // (no <Duration> means indefinite repetition) - the same shape
            // `schtasks /Create /SC HOURLY` itself produces, rather than a
            // daily trigger with an hourly repetition wrapped around it.
            "hourly" => new XElement(TaskNs + "TimeTrigger",
                new XElement(TaskNs + "StartBoundary", startBoundary),
                new XElement(TaskNs + "Enabled", "true"),
                new XElement(TaskNs + "Repetition",
                    new XElement(TaskNs + "Interval", "PT1H"),
                    new XElement(TaskNs + "StopAtDurationEnd", "false"))),
            _ => new XElement(TaskNs + "CalendarTrigger",
                new XElement(TaskNs + "StartBoundary", startBoundary),
                new XElement(TaskNs + "Enabled", "true"),
                new XElement(TaskNs + "ScheduleByDay",
                    new XElement(TaskNs + "DaysInterval", "1"))),
        };

        var settings = new XElement(TaskNs + "Settings",
            new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(TaskNs + "DisallowStartIfOnBatteries", Bool(schedule.DisallowStartIfOnBatteries)),
            new XElement(TaskNs + "StopIfGoingOnBatteries", Bool(schedule.StopIfGoingOnBatteries)),
            new XElement(TaskNs + "AllowHardTerminate", "true"),
            new XElement(TaskNs + "StartWhenAvailable", Bool(schedule.StartWhenAvailable)),
            new XElement(TaskNs + "RunOnlyIfNetworkAvailable", Bool(schedule.RunOnlyIfNetworkAvailable)),
            new XElement(TaskNs + "AllowStartOnDemand", "true"),
            new XElement(TaskNs + "Enabled", "true"),
            new XElement(TaskNs + "Hidden", "false"),
            new XElement(TaskNs + "RunOnlyIfIdle", "false"),
            new XElement(TaskNs + "WakeToRun", "false"),
            new XElement(TaskNs + "ExecutionTimeLimit", "PT0S"),
            new XElement(TaskNs + "Priority", "7"));

        if (schedule.RestartOnFailure)
        {
            settings.Add(new XElement(TaskNs + "RestartOnFailure",
                new XElement(TaskNs + "Interval", $"PT{Math.Max(1, schedule.RestartIntervalMinutes)}M"),
                new XElement(TaskNs + "Count", Math.Max(1, schedule.RestartCount).ToString(CultureInfo.InvariantCulture))));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(TaskNs + "Task", new XAttribute("version", "1.2"),
                new XElement(TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Description", "Runs the ClaudeCounter backup worker on a schedule.")),
                new XElement(TaskNs + "Triggers", trigger),
                new XElement(TaskNs + "Principals",
                    new XElement(TaskNs + "Principal", new XAttribute("id", "Author"),
                        new XElement(TaskNs + "LogonType", "InteractiveToken"),
                        new XElement(TaskNs + "RunLevel", "LeastPrivilege"))),
                settings,
                new XElement(TaskNs + "Actions", new XAttribute("Context", "Author"),
                    new XElement(TaskNs + "Exec",
                        new XElement(TaskNs + "Command", workerPath)))));

        return doc.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// Registers (or replaces) the scheduled task by generating the task
    /// definition XML (see <see cref="BuildTaskXml"/>), writing it to a temp
    /// file, and calling <c>schtasks /Create /F /TN ... /XML &lt;file&gt;</c>
    /// - the only way to set the advanced settings BuildTaskXml encodes
    /// (StartWhenAvailable, battery behaviour, retry on failure), none of
    /// which `schtasks /Create` exposes as flags. The temp file is written
    /// as UTF-16 to match the XML declaration BuildTaskXml emits, and is
    /// always deleted afterwards - including when schtasks itself fails -
    /// since nothing else in this tool ever revisits it. Never throws;
    /// returns false on any failure (worker absent, invalid schedule, the
    /// temp file could not be written, or a non-zero schtasks exit), with
    /// the reason logged via Log.Warn.
    /// </summary>
    public static bool Register(ScheduleConfig schedule)
    {
        if (WorkerPath() is not { } path)
            return false;

        string xml;
        try
        {
            xml = BuildTaskXml(schedule, path);
        }
        catch (ArgumentException e)
        {
            Log.Warn($"Refused to register the backup schedule: {e.Message}");
            return false;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"claudecounter-backup-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempFile, xml, Encoding.Unicode);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not write the task definition file: {e.Message}");
            return false;
        }

        try
        {
            return Run($"/Create /F /TN \"{TaskName}\" /XML \"{tempFile}\"");
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch (Exception e)
            {
                Log.Warn($"Could not delete temp task definition file '{tempFile}': {e.Message}");
            }
        }
    }

    /// <summary>
    /// Removes the scheduled task. Distinguishes "there was nothing to
    /// remove" from a genuine failure so a caller does not have to report a
    /// fresh install's default (both destinations disabled) as an error the
    /// first time a user touches the Save button. Never throws.
    /// </summary>
    public static UnregisterOutcome Unregister()
    {
        // schtasks /Delete on a task that was never created exits non-zero,
        // which would otherwise look identical to a real deletion failure.
        // Query first (exit code only - never parse schtasks' locale-
        // dependent text output) and only attempt /Delete when the task is
        // known to exist. If the query itself could not be answered (null:
        // schtasks failed to launch), fall through to attempting /Delete
        // anyway rather than silently assuming "nothing to remove" - a
        // genuine environment problem must still surface as a failure.
        if (QueryTaskExists() == false)
            return UnregisterOutcome.NotFound;
        return Run($"/Delete /F /TN \"{TaskName}\"") ? UnregisterOutcome.Removed : UnregisterOutcome.Failed;
    }

    /// <summary>
    /// Builds the schtasks.exe command-line argument string for checking
    /// whether the scheduled task exists. Pure and unit-testable - performs
    /// no I/O.
    /// </summary>
    public static string BuildQueryArgs() => $"/Query /TN \"{TaskName}\"";

    /// <summary>
    /// True if the task exists, false if it does not, or null if this could
    /// not be determined (schtasks itself failed to launch). Relies solely on
    /// the exit code - never parses schtasks' locale-dependent text output.
    /// </summary>
    private static bool? QueryTaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", BuildQueryArgs())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            // M1: both streams are redirected, so reading one to completion
            // synchronously before waiting risks the classic pipe-buffer
            // deadlock - if schtasks fills the OTHER stream's OS pipe buffer
            // while this process is blocked reading the first one (or
            // blocked in WaitForExit with neither stream being drained),
            // both sides stall forever. Starting async reads for both before
            // WaitForExit keeps both pipes drained regardless of how much
            // each stream produces or in what order.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            stdoutTask.Wait();
            stderrTask.Wait();
            return p.ExitCode == 0;
        }
        catch (Exception e)
        {
            Log.Warn($"schtasks query failed: {e.Message}");
            return null;
        }
    }

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
            if (p is null)
                return false;
            // M1: same reasoning as QueryTaskExists above - read both
            // redirected streams asynchronously, started before WaitForExit,
            // rather than blocking on ReadToEnd after the process has
            // already exited (too late to prevent a deadlock if it never
            // exits because a full pipe buffer is blocking it).
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            stdoutTask.Wait();
            var stderr = stderrTask.Result;
            if (p.ExitCode != 0)
            {
                Log.Warn($"schtasks exited {p.ExitCode}: {stderr.Trim()}");
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

/// <summary>Outcome of BackupTaskManager.Unregister().</summary>
public enum UnregisterOutcome
{
    /// <summary>A task existed and was successfully deleted.</summary>
    Removed,

    /// <summary>No task existed - nothing needed to happen. Not an error.</summary>
    NotFound,

    /// <summary>A task existed (or its existence could not be determined) and deletion failed.</summary>
    Failed,
}

using ClaudeBackup;
using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// BackupTaskManager.BuildSchtasksArgs is the pure, testable core of the
/// tray's Task Scheduler integration. WorkerPath/Register/RunNow touch the
/// real filesystem and process table and are deliberately not exercised here.
/// </summary>
public class BackupTaskManagerTests
{
    [Fact]
    public void DailyArgsUseScDaily()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("/SC", args);
        Assert.Contains("DAILY", args);
        Assert.Contains("09:00", args);
        Assert.Contains("ClaudeBackup.exe", args);
        Assert.Contains("ClaudeCounter Backup", args); // task name
    }

    [Fact]
    public void WeeklyMapsToScWeekly()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "weekly", Time = "18:30" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("WEEKLY", args);
        Assert.DoesNotContain("HOURLY", args);
        Assert.DoesNotContain("DAILY", args);
    }

    [Fact]
    public void HourlyMapsToScHourly()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "hourly", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("HOURLY", args);
    }

    [Fact]
    public void UnrecognizedFrequencyFallsBackToDaily()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "fortnightly", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("DAILY", args);
    }

    [Fact]
    public void FrequencyMatchIsCaseInsensitive()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "HOURLY", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("HOURLY", args);
    }

    [Fact]
    public void IncludesTaskNameFlag()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("/TN \"ClaudeCounter Backup\"", args);
    }

    [Fact]
    public void IncludesForceOverwriteFlag()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("/F", args);
    }

    [Fact]
    public void IncludesStartTimeFlag()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = "14:45" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("/ST 14:45", args);
    }

    [Fact]
    public void PathWithSpacesIsQuotedInsideTheTrArgument()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = "09:00" },
            @"C:\Users\First Last\AppData\Local\ClaudeCounter\ClaudeBackup.exe");

        // The whole /TR value must be one shell-visible token (outer quotes),
        // and the path inside it must carry its own escaped quotes so the
        // consumer (schtasks, and later Task Scheduler) can find the exe
        // boundary even though the path itself contains a space.
        Assert.Contains(
            "/TR \"\\\"C:\\Users\\First Last\\AppData\\Local\\ClaudeCounter\\ClaudeBackup.exe\\\"\"",
            args);
    }

    [Fact]
    public void NeverRequestsElevationOrARemoteSystem()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.DoesNotContain("/RU", args);
        Assert.DoesNotContain("/S ", args);
    }
}

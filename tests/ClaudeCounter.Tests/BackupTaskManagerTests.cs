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

    // Time reaches BuildSchtasksArgs as free text from a Settings textbox.
    // Without strict HH:mm validation, a crafted value could splice extra
    // schtasks arguments (e.g. a second /TR) into the command line.
    [Theory]
    [InlineData("09:00\" /TR \"calc.exe")]
    [InlineData("9:00")]           // hour must be two digits
    [InlineData("09:0")]           // minute must be two digits
    [InlineData("24:00")]          // hour out of range
    [InlineData("09:60")]          // minute out of range
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("09:00 ")]         // trailing content, even whitespace, is refused
    [InlineData("not a time")]
    public void RejectsAnyTimeThatIsNotStrictTwentyFourHourHhMm(string time)
    {
        var ex = Assert.Throws<ArgumentException>(() => BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = time },
            @"C:\apps\ClaudeBackup.exe"));
        Assert.Contains(time, ex.Message);
    }

    [Theory]
    [InlineData("00:00")]
    [InlineData("23:59")]
    [InlineData("09:05")]
    public void AcceptsValidTwentyFourHourHhMm(string time)
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = time },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains($"/ST {time}", args);
    }

    // BuildQueryArgs backs Unregister()'s "does the task even exist" check
    // (schtasks /Query, exit code only, no text parsing). Query/Register both
    // use the fixed task name, so it must be quoted the same way in both.
    [Fact]
    public void QueryArgsUseQueryFlag()
    {
        var args = BackupTaskManager.BuildQueryArgs();
        Assert.Contains("/Query", args);
    }

    [Fact]
    public void QueryArgsQuoteTheTaskNameBecauseItContainsASpace()
    {
        var args = BackupTaskManager.BuildQueryArgs();
        Assert.Contains("/TN \"ClaudeCounter Backup\"", args);
    }

    [Fact]
    public void QueryArgsDoNotIncludeCreateOrDeleteFlags()
    {
        var args = BackupTaskManager.BuildQueryArgs();
        Assert.DoesNotContain("/Create", args);
        Assert.DoesNotContain("/Delete", args);
    }
}

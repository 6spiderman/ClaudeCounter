using System.Xml.Linq;
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

    // I1: ResultMessage is the pure mapping RunNowAsync's caller uses to
    // decide what to show the user - exercised directly here since RunNowAsync
    // itself launches a real process and is not something a unit test should
    // invoke against a real worker.
    [Theory]
    [InlineData(0, "Backup complete.")]
    [InlineData(1, "Backup not run - check your backup settings.")]
    [InlineData(2, "Backup failed - see the log.")]
    public void ResultMessageMapsKnownExitCodes(int exitCode, string expected) =>
        Assert.Equal(expected, BackupTaskManager.ResultMessage(exitCode));

    [Fact]
    public void ResultMessageHandlesNullAsCouldNotStart()
    {
        var message = BackupTaskManager.ResultMessage(null);
        Assert.Contains("Could not start", message);
    }

    // RunNowAsync itself is deliberately not exercised here: this test
    // project has a ProjectReference to ClaudeBackup.csproj (so
    // ClaudeCounter.csproj can be referenced without it - see this file's
    // header - which means ClaudeBackup.exe is copied next to the test host,
    // and WorkerPath() (a plain File.Exists check beside the running exe)
    // finds it. Calling RunNowAsync from a unit test would therefore launch
    // the REAL worker against this machine's real backup.json - exactly the
    // kind of real-process invocation this project's tests must not do.
    // ResultMessage above is RunNowAsync's entire pure, testable surface.
}

/// <summary>
/// BackupTaskManager.BuildTaskXml is where registration's real logic now
/// lives (schedule-robustness design spec, Part 1) - schtasks /Create with
/// flags cannot express StartWhenAvailable, the battery settings, or retry
/// on failure, so registration switched to schtasks /Create /XML &lt;file&gt;
/// with the definition generated here. Every test round-trips the result
/// through XDocument.Parse (never string-matching alone) so a malformed
/// document - wrong namespace, unbalanced elements, bad XML - fails the test
/// rather than slipping through on a substring match. Register itself (which
/// writes the temp file and invokes schtasks) is not exercised here, for the
/// same reason BuildSchtasksArgs's tests above never exercise Register.
/// </summary>
public class BackupTaskManagerBuildTaskXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Parse(string xml) => XDocument.Parse(xml);

    private static ScheduleConfig Daily(string time = "09:00") => new() { Frequency = "daily", Time = time };

    [Fact]
    public void ProducesWellFormedXmlInTheTaskSchedulerNamespace()
    {
        var xml = BackupTaskManager.BuildTaskXml(Daily(), @"C:\apps\ClaudeBackup.exe");
        var doc = Parse(xml);
        Assert.Equal(Ns + "Task", doc.Root!.Name);
        Assert.Equal("1.2", doc.Root.Attribute("version")?.Value);
    }

    [Fact]
    public void CommandElementCarriesTheWorkerPathVerbatim()
    {
        var xml = BackupTaskManager.BuildTaskXml(Daily(), @"C:\apps\ClaudeBackup.exe");
        var doc = Parse(xml);
        var command = doc.Descendants(Ns + "Command").Single().Value;
        Assert.Equal(@"C:\apps\ClaudeBackup.exe", command);
    }

    // Unlike BuildSchtasksArgs' /TR value, an XML element's text content
    // needs no manual quoting for an embedded space - XElement's own
    // escaping is all that is required, and the round-tripped value must
    // come back exactly as passed in, spaces included.
    [Fact]
    public void CommandElementNeedsNoEscapingForAPathWithSpaces()
    {
        const string path = @"C:\Users\First Last\AppData\Local\ClaudeCounter\ClaudeBackup.exe";
        var xml = BackupTaskManager.BuildTaskXml(Daily(), path);
        var doc = Parse(xml);
        Assert.Equal(path, doc.Descendants(Ns + "Command").Single().Value);
    }

    [Fact]
    public void DailyUsesCalendarTriggerWithScheduleByDay()
    {
        var xml = BackupTaskManager.BuildTaskXml(Daily(), @"C:\apps\ClaudeBackup.exe");
        var doc = Parse(xml);
        var trigger = doc.Descendants(Ns + "CalendarTrigger").Single();
        Assert.Equal("1", trigger.Descendants(Ns + "ScheduleByDay").Single()
            .Element(Ns + "DaysInterval")!.Value);
        Assert.Contains("09:00:00", trigger.Element(Ns + "StartBoundary")!.Value);
    }

    [Fact]
    public void WeeklyUsesCalendarTriggerWithScheduleByWeek()
    {
        var xml = BackupTaskManager.BuildTaskXml(
            new ScheduleConfig { Frequency = "weekly", Time = "18:30" }, @"C:\apps\ClaudeBackup.exe");
        var doc = Parse(xml);
        var trigger = doc.Descendants(Ns + "CalendarTrigger").Single();
        var byWeek = trigger.Element(Ns + "ScheduleByWeek")!;
        Assert.Equal("1", byWeek.Element(Ns + "WeeksInterval")!.Value);
        // Recurs on whatever day of the week "now" (registration time) is -
        // exactly one day-of-week element, matching DateTime.Now.DayOfWeek.
        var daysOfWeek = byWeek.Element(Ns + "DaysOfWeek")!;
        Assert.Single(daysOfWeek.Elements());
        Assert.Equal(Ns + DateTime.Now.DayOfWeek.ToString(), daysOfWeek.Elements().Single().Name);
    }

    [Fact]
    public void HourlyUsesTimeTriggerWithHourlyRepetition()
    {
        var xml = BackupTaskManager.BuildTaskXml(
            new ScheduleConfig { Frequency = "hourly", Time = "09:00" }, @"C:\apps\ClaudeBackup.exe");
        var doc = Parse(xml);
        Assert.Empty(doc.Descendants(Ns + "CalendarTrigger"));
        var trigger = doc.Descendants(Ns + "TimeTrigger").Single();
        var repetition = trigger.Element(Ns + "Repetition")!;
        Assert.Equal("PT1H", repetition.Element(Ns + "Interval")!.Value);
        Assert.Equal("false", repetition.Element(Ns + "StopAtDurationEnd")!.Value);
    }

    [Fact]
    public void UnrecognizedFrequencyFallsBackToDaily()
    {
        var xml = BackupTaskManager.BuildTaskXml(
            new ScheduleConfig { Frequency = "fortnightly", Time = "09:00" }, @"C:\apps\ClaudeBackup.exe");
        var doc = Parse(xml);
        Assert.Single(doc.Descendants(Ns + "CalendarTrigger"));
        Assert.NotNull(doc.Descendants(Ns + "ScheduleByDay").SingleOrDefault());
    }

    [Fact]
    public void DefaultsMatchTheDesignSpec()
    {
        // ScheduleConfig's own field initializers ARE the design spec's
        // default table - this proves BuildTaskXml faithfully encodes
        // whatever ScheduleConfig says, using an untouched instance.
        var xml = BackupTaskManager.BuildTaskXml(new ScheduleConfig(), @"C:\apps\ClaudeBackup.exe");
        var settings = Parse(xml).Descendants(Ns + "Settings").Single();

        Assert.Equal("true", settings.Element(Ns + "StartWhenAvailable")!.Value);
        Assert.Equal("true", settings.Element(Ns + "RunOnlyIfNetworkAvailable")!.Value);
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);

        var restart = settings.Element(Ns + "RestartOnFailure")!;
        Assert.Equal("PT15M", restart.Element(Ns + "Interval")!.Value);
        Assert.Equal("3", restart.Element(Ns + "Count")!.Value);
    }

    [Fact]
    public void HonoursNonDefaultBatteryAndNetworkSettings()
    {
        var schedule = Daily();
        schedule.StartWhenAvailable = false;
        schedule.RunOnlyIfNetworkAvailable = false;
        schedule.DisallowStartIfOnBatteries = true;
        schedule.StopIfGoingOnBatteries = true;

        var settings = Parse(BackupTaskManager.BuildTaskXml(schedule, @"C:\apps\ClaudeBackup.exe"))
            .Descendants(Ns + "Settings").Single();

        Assert.Equal("false", settings.Element(Ns + "StartWhenAvailable")!.Value);
        Assert.Equal("false", settings.Element(Ns + "RunOnlyIfNetworkAvailable")!.Value);
        Assert.Equal("true", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("true", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
    }

    [Fact]
    public void HonoursNonDefaultRestartIntervalAndCount()
    {
        var schedule = Daily();
        schedule.RestartIntervalMinutes = 45;
        schedule.RestartCount = 7;

        var restart = Parse(BackupTaskManager.BuildTaskXml(schedule, @"C:\apps\ClaudeBackup.exe"))
            .Descendants(Ns + "RestartOnFailure").Single();

        Assert.Equal("PT45M", restart.Element(Ns + "Interval")!.Value);
        Assert.Equal("7", restart.Element(Ns + "Count")!.Value);
    }

    [Fact]
    public void OmitsRestartOnFailureElementWhenDisabled()
    {
        var schedule = Daily();
        schedule.RestartOnFailure = false;

        var xml = BackupTaskManager.BuildTaskXml(schedule, @"C:\apps\ClaudeBackup.exe");
        Assert.Empty(Parse(xml).Descendants(Ns + "RestartOnFailure"));
    }

    // Same invalid-time inputs BuildSchtasksArgs is checked against - the
    // validation is shared (see BackupTaskManager.ParseTimeOrThrow), but
    // this proves BuildTaskXml itself actually calls it rather than
    // silently accepting whatever reaches it.
    [Theory]
    [InlineData("9:00")]
    [InlineData("09:0")]
    [InlineData("24:00")]
    [InlineData("09:60")]
    [InlineData("")]
    [InlineData("not a time")]
    public void RejectsAnyTimeThatIsNotStrictTwentyFourHourHhMm(string time)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BackupTaskManager.BuildTaskXml(new ScheduleConfig { Frequency = "daily", Time = time },
                @"C:\apps\ClaudeBackup.exe"));
        Assert.Contains(time, ex.Message);
    }

    [Theory]
    [InlineData("00:00")]
    [InlineData("23:59")]
    [InlineData("09:05")]
    public void AcceptsValidTwentyFourHourHhMm(string time)
    {
        var xml = BackupTaskManager.BuildTaskXml(Daily(time), @"C:\apps\ClaudeBackup.exe");
        var doc = Parse(xml);
        Assert.Contains(time + ":00", doc.Descendants(Ns + "StartBoundary").Single().Value);
    }
}

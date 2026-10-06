using System.Text.Json;
using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests;

public class UsageBandsTests
{
    [Theory]
    [InlineData(0, UsageBand.Green)]
    [InlineData(74.9, UsageBand.Green)]
    [InlineData(75, UsageBand.Amber)]
    [InlineData(89.9, UsageBand.Amber)]
    [InlineData(90, UsageBand.Red)]
    [InlineData(100, UsageBand.Red)]
    public void BandsFollowTheThresholds(double utilization, UsageBand expected) =>
        Assert.Equal(expected, UsageBands.For(utilization, 75, 90));

    [Fact]
    public void NamesAreTheLowercaseJsonValues()
    {
        Assert.Equal("green", UsageBands.Name(UsageBand.Green));
        Assert.Equal("amber", UsageBands.Name(UsageBand.Amber));
        Assert.Equal("red", UsageBands.Name(UsageBand.Red));
        Assert.Equal("gray", UsageBands.Name(UsageBand.Gray));
    }
}

public sealed class UsageStatusTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-status-" + Guid.NewGuid().ToString("N"));

    private string StatusPath => Path.Combine(_dir, "usage-status.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static UsageSnapshot Snapshot(double fiveHour = 42, double week = 25, double? opus = null) => new(
        new UsageWindow(fiveHour, Now.AddHours(1).AddMinutes(12)),
        new UsageWindow(week, Now.AddDays(2).AddHours(19)),
        opus is { } o ? new UsageWindow(o, null) : null,
        null,
        new ExtraUsage(true, 95m, 2.21m, 2.3));

    private static UsageStatusFile File(
        UsageSnapshot? snapshot = null, string problem = "none", DateTimeOffset? updatedAt = null, int interval = 5) => new(
        UsageStatusFile.CurrentVersion, "1.3.0", updatedAt ?? Now, Now, interval, problem, null, "own", 75, 90,
        snapshot ?? Snapshot());

    [Fact]
    public void FromPollStateCarriesTheNumbersButNoAccountDetails()
    {
        var state = new PollState
        {
            Snapshot = Snapshot(),
            LastSuccessAt = Now,
            Problem = ProblemKind.RateLimited,
            ProblemMessage = "slow down",
            Source = TokenSource.CliBootstrap,
        };

        var file = UsageStatusFile.From(state, 10, 70, 95, Now);

        Assert.Equal(UsageStatusFile.CurrentVersion, file.Version);
        Assert.Equal(Now, file.UpdatedAt);
        Assert.Equal(10, file.PollIntervalMinutes);
        Assert.Equal("rateLimited", file.Problem);
        Assert.Equal("claudeCode", file.Source);
        Assert.Equal(70, file.WarnThreshold);
        Assert.Equal(95, file.CriticalThreshold);
        Assert.Same(state.Snapshot, file.Snapshot);
    }

    [Theory]
    [InlineData(TokenSource.OwnSession, "own")]
    [InlineData(TokenSource.CliBootstrap, "claudeCode")]
    [InlineData(TokenSource.EnvironmentVariable, "environment")]
    [InlineData(null, "none")]
    public void SourceNames(TokenSource? source, string expected) =>
        Assert.Equal(expected, UsageStatusFile.SourceName(source));

    [Fact]
    public void WriteThenReadRoundTrips()
    {
        var written = File(Snapshot(opus: 12));
        UsageStatusStore.Write(written, StatusPath);

        var read = UsageStatusStore.Read(StatusPath);

        Assert.NotNull(read);
        Assert.Equal(written with { Snapshot = null }, read! with { Snapshot = null });
        Assert.Equal(written.Snapshot, read.Snapshot);
        Assert.False(System.IO.File.Exists(StatusPath + ".tmp"));
    }

    [Fact]
    public void TheFileIsCamelCaseAndHoldsNoTokenOrEmail()
    {
        UsageStatusStore.Write(File(), StatusPath);
        var json = System.IO.File.ReadAllText(StatusPath);

        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("updatedAt", names);
        Assert.Contains("snapshot", names);
        Assert.DoesNotContain(names, n => n.Contains("token", StringComparison.OrdinalIgnoreCase)
            || n.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    [UnixOnlyFact]
    public void OnLinuxTheFileIsOwnerOnly()
    {
        UsageStatusStore.Write(File(), StatusPath);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(StatusPath));
    }

    [Fact]
    public void AMissingOrBrokenFileReadsAsNull()
    {
        Assert.Null(UsageStatusStore.Read(StatusPath));
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(StatusPath, "{ not json");
        Assert.Null(UsageStatusStore.Read(StatusPath));
        System.IO.File.WriteAllText(StatusPath, """{"version": 99}""");
        Assert.Null(UsageStatusStore.Read(StatusPath));
    }

    [Fact]
    public void AFreshFileFromARunningAppIsNotStale()
    {
        var report = UsageStatusReport.From(File(updatedAt: Now.AddMinutes(-14)), running: true, Now);

        Assert.False(report.Stale);
        Assert.True(report.Available);
        Assert.Equal("5h 42% (1 h 12 min) | week 25% (2 d 19 h)", report.Line);
        Assert.Equal(["green", "green"], report.Windows.Select(w => w.Band));
    }

    [Theory]
    [InlineData(5, 16, true)]   // at least 15 minutes, even with a short interval
    [InlineData(5, 14, false)]
    [InlineData(30, 59, false)] // two intervals
    [InlineData(30, 61, true)]
    public void StalenessIsTwoIntervalsWithAFifteenMinuteFloor(int interval, int ageMinutes, bool stale) =>
        Assert.Equal(stale, UsageStatusReport.IsStale(File(updatedAt: Now.AddMinutes(-ageMinutes), interval: interval), true, Now));

    [Fact]
    public void OldNumbersAreMarkedStaleAndGray()
    {
        var report = UsageStatusReport.From(File(updatedAt: Now.AddHours(-1)), running: true, Now);

        Assert.True(report.Stale);
        Assert.True(report.Available);
        Assert.EndsWith(" | stale", report.Line, StringComparison.Ordinal);
        Assert.All(report.Windows, w => Assert.Equal("gray", w.Band));
    }

    [Fact]
    public void WhenTheAppIsNotRunningTheLastNumbersAreStillShownButFlagged()
    {
        var report = UsageStatusReport.From(File(), running: false, Now);

        Assert.False(report.Running);
        Assert.True(report.Stale);
        Assert.True(report.Available);
        Assert.Equal("5h 42% (1 h 12 min) | week 25% (2 d 19 h) | not running", report.Line);
    }

    [Fact]
    public void NoFileAtAll()
    {
        Assert.Equal("ClaudeCounter: not running", UsageStatusReport.From(null, false, Now).Line);
        var running = UsageStatusReport.From(null, true, Now);
        Assert.Equal("ClaudeCounter: no usage yet", running.Line);
        Assert.False(running.Available);
    }

    [Fact]
    public void SignedOutShowsNotSignedIn()
    {
        var file = File(problem: "signInRequired") with { Snapshot = null };
        var report = UsageStatusReport.From(file, running: true, Now);

        Assert.False(report.Available);
        Assert.Equal("ClaudeCounter: not signed in", report.Line);
    }

    [Fact]
    public void AProblemKeepsTheNumbersButGraysThem()
    {
        var report = UsageStatusReport.From(File(problem: "network"), running: true, Now);

        Assert.True(report.Available);
        Assert.False(report.Stale);
        Assert.All(report.Windows, w => Assert.Equal("gray", w.Band));
    }

    [Fact]
    public void BandsUseThePublishedThresholds()
    {
        var report = UsageStatusReport.From(File(Snapshot(fiveHour: 91, week: 80)), running: true, Now);
        Assert.Equal(["red", "amber"], report.Windows.Select(w => w.Band));
    }

    [Fact]
    public void ModelWindowsAreListedButKeptOffTheLine()
    {
        var report = UsageStatusReport.From(File(Snapshot(opus: 12)), running: true, Now);

        Assert.Equal(["fiveHour", "sevenDay", "sevenDayOpus"], report.Windows.Select(w => w.Key));
        var opus = report.Windows[2];
        Assert.Equal("Weekly (Opus)", opus.Label);
        Assert.Null(opus.ResetsIn);
        Assert.DoesNotContain("Opus", report.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonHasWhatTheWidgetReads()
    {
        using var doc = JsonDocument.Parse(UsageStatusReport.From(File(), running: true, Now).ToJson());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("running").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("none", root.GetProperty("problem").GetString());
        var first = root.GetProperty("windows")[0];
        Assert.Equal("fiveHour", first.GetProperty("key").GetString());
        Assert.Equal(42, first.GetProperty("utilization").GetDouble());
        Assert.Equal("1 h 12 min", first.GetProperty("resetsIn").GetString());
        Assert.Equal("green", first.GetProperty("band").GetString());
        Assert.True(root.GetProperty("extraUsage").GetProperty("isEnabled").GetBoolean());
    }
}

public class CommandLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static UsageStatusFile Status() => new(
        UsageStatusFile.CurrentVersion, "1.3.0", Now, Now, 5, "none", null, "own", 75, 90,
        new UsageSnapshot(new UsageWindow(42, Now.AddMinutes(30)), null, null, null, null));

    private static (int Code, string Out, string Err, int Refreshes) Run(
        string[] args, bool running = true, UsageStatusFile? status = null, bool refreshAnswers = true)
    {
        var refreshes = 0;
        var env = new CommandLine.Environment(
            () => running,
            () => status,
            () => { refreshes++; return refreshAnswers; },
            () => Now);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(args, output, error, env);
        return (code, output.ToString().Trim(), error.ToString().Trim(), refreshes);
    }

    [Fact]
    public void NoArgumentsStartsTheApp() => Assert.False(CommandLine.IsCommand([]));

    [Fact]
    public void StatusPrintsTheLine()
    {
        var (code, output, _, _) = Run(["--status"], status: Status());
        Assert.Equal(CommandLine.Ok, code);
        Assert.Equal("5h 42% (30 min)", output);
    }

    [Fact]
    public void StatusJsonPrintsJsonEvenWhenNothingIsAvailable()
    {
        var (code, output, _, _) = Run(["--status", "--json"], running: false);
        Assert.Equal(CommandLine.Unavailable, code);
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("running").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
    }

    [Fact]
    public void OptionOrderDoesNotMatter() =>
        Assert.Equal(CommandLine.Ok, Run(["--json", "--status"], status: Status()).Code);

    [Fact]
    public void StatusWithNothingToShowExitsOne()
    {
        var (code, output, _, _) = Run(["--status"], running: false);
        Assert.Equal(CommandLine.Unavailable, code);
        Assert.Equal("ClaudeCounter: not running", output);
    }

    [Fact]
    public void RefreshAsksTheRunningApp()
    {
        var (code, _, _, refreshes) = Run(["--refresh"]);
        Assert.Equal(CommandLine.Ok, code);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void RefreshWithNothingRunningExitsOne()
    {
        var (code, _, error, _) = Run(["--refresh"], refreshAnswers: false);
        Assert.Equal(CommandLine.Unavailable, code);
        Assert.Contains("not running", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("status")]
    public void UnknownOptionsAreAUsageError(string arg)
    {
        var (code, output, error, refreshes) = Run([arg]);
        Assert.Equal(CommandLine.UsageError, code);
        Assert.Empty(output);
        Assert.Contains(arg, error, StringComparison.Ordinal);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void JsonAloneIsAUsageError() => Assert.Equal(CommandLine.UsageError, Run(["--json"]).Code);

    [Fact]
    public void RefreshCombinedWithStatusIsAUsageError() =>
        Assert.Equal(CommandLine.UsageError, Run(["--refresh", "--status"]).Code);

    [Fact]
    public void HelpAndVersion()
    {
        Assert.Contains("--status", Run(["--help"]).Out, StringComparison.Ordinal);
        Assert.StartsWith("ClaudeCounter ", Run(["--version"]).Out, StringComparison.Ordinal);
    }
}

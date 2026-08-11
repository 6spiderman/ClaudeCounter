using ClaudeBackup;
using ClaudeCounter.Notifications;
using Xunit;

namespace ClaudeCounter.Tests;

public class BackupHealthPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static DestinationHealth Healthy(string name, DateTimeOffset lastSuccess) =>
        new(name, DestinationHealthState.Healthy, lastSuccess, lastSuccess, "");

    private static DestinationHealth Failed(string name, DateTimeOffset? lastSuccess = null) =>
        new(name, DestinationHealthState.Failed, lastSuccess, Now, "backend failed");

    private static DestinationHealth NeverRun(string name) =>
        new(name, DestinationHealthState.NeverRun, null, null, "");

    private static DestinationHealth Stale(string name, DateTimeOffset lastSuccess) =>
        new(name, DestinationHealthState.Stale, lastSuccess, lastSuccess, "");

    // --- TooltipLine -------------------------------------------------------

    [Fact]
    public void TooltipLineIsNullWhenTheWorkerIsNotInstalled()
    {
        Assert.Null(BackupHealthPresenter.TooltipLine(null));
    }

    [Fact]
    public void TooltipLineIsNullWhenNotConfigured()
    {
        var result = new BackupHealthResult(BackupHealthState.NotConfigured, Array.Empty<DestinationHealth>());
        Assert.Null(BackupHealthPresenter.TooltipLine(result));
    }

    [Fact]
    public void TooltipLineIsNullWhenHealthy()
    {
        var result = new BackupHealthResult(BackupHealthState.Healthy, new[] { Healthy("GitHub", Now) });
        Assert.Null(BackupHealthPresenter.TooltipLine(result));
    }

    [Theory]
    [InlineData(BackupHealthState.NeverRun, "Backup: never run")]
    [InlineData(BackupHealthState.Failed, "Backup: failed")]
    [InlineData(BackupHealthState.Stale, "Backup: stale")]
    public void TooltipLineNamesTheProblemForEveryWarrantingState(BackupHealthState state, string expected)
    {
        var result = new BackupHealthResult(state, new[] { Failed("GitHub") });
        Assert.Equal(expected, BackupHealthPresenter.TooltipLine(result));
    }

    // The tooltip's whole point is staying inside TrayApplicationContext's
    // 127-char budget - pin the line itself to something short so a future
    // edit cannot silently make it long enough to threaten that budget.
    [Fact]
    public void TooltipLineStaysShort()
    {
        var result = new BackupHealthResult(BackupHealthState.Failed, new[] { Failed("GitHub") });
        var line = BackupHealthPresenter.TooltipLine(result);
        Assert.NotNull(line);
        Assert.True(line!.Length <= 20, $"Tooltip backup line was {line!.Length} chars: '{line}'.");
    }

    // --- FlyoutLines ---------------------------------------------------------

    [Fact]
    public void FlyoutLinesAreEmptyWhenTheWorkerIsNotInstalled()
    {
        Assert.Empty(BackupHealthPresenter.FlyoutLines(null, Now));
    }

    // Absent entirely when NotConfigured - never nag a user who does not use
    // backup (the design spec's own words).
    [Fact]
    public void FlyoutLinesAreEmptyWhenNotConfigured()
    {
        var result = new BackupHealthResult(BackupHealthState.NotConfigured, Array.Empty<DestinationHealth>());
        Assert.Empty(BackupHealthPresenter.FlyoutLines(result, Now));
    }

    [Fact]
    public void FlyoutLinesShowJustTheSummaryWhenHealthy()
    {
        var result = new BackupHealthResult(BackupHealthState.Healthy, new[] { Healthy("GitHub", Now.AddHours(-1)) });
        var lines = BackupHealthPresenter.FlyoutLines(result, Now);
        Assert.Single(lines);
        Assert.Contains("healthy", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlyoutLinesNameTheBrokenDestinationAndLastSuccessForFailed()
    {
        var result = new BackupHealthResult(
            BackupHealthState.Failed,
            new[] { Failed("GitHub", Now.AddDays(-2)), Healthy("Google Drive", Now.AddHours(-1)) });
        var lines = BackupHealthPresenter.FlyoutLines(result, Now);

        Assert.Contains(lines, l => l.Contains("failed", StringComparison.OrdinalIgnoreCase));
        var detail = Assert.Single(lines, l => l.StartsWith("GitHub", StringComparison.Ordinal));
        Assert.Contains("last succeeded", detail);
        // Google Drive is healthy, so it must not get its own detail line.
        Assert.DoesNotContain(lines, l => l.StartsWith("Google Drive", StringComparison.Ordinal));
    }

    // NeverRun must say "never succeeded", not blow up on a null LastSuccessUtc.
    [Fact]
    public void FlyoutLinesSayNeverSucceededForANeverRunDestination()
    {
        var result = new BackupHealthResult(BackupHealthState.NeverRun, new[] { NeverRun("GitHub") });
        var lines = BackupHealthPresenter.FlyoutLines(result, Now);

        var detail = Assert.Single(lines, l => l.StartsWith("GitHub", StringComparison.Ordinal));
        Assert.Contains("never succeeded", detail);
    }

    [Fact]
    public void FlyoutLinesIncludeEveryNonHealthyDestinationWhenStale()
    {
        var result = new BackupHealthResult(
            BackupHealthState.Stale,
            new[] { Stale("GitHub", Now.AddDays(-10)), NeverRun("Google Drive") });
        var lines = BackupHealthPresenter.FlyoutLines(result, Now);

        Assert.Equal(3, lines.Count); // summary + 2 destination details
        Assert.Contains(lines, l => l.StartsWith("GitHub", StringComparison.Ordinal) && l.Contains("last succeeded"));
        Assert.Contains(lines, l => l.StartsWith("Google Drive", StringComparison.Ordinal) && l.Contains("never succeeded"));
    }

    // --- PopupContent --------------------------------------------------------

    [Fact]
    public void PopupContentNamesTheDestinationForFailed()
    {
        var result = new BackupHealthResult(BackupHealthState.Failed, new[] { Failed("GitHub", Now.AddDays(-1)) });
        var (title, body) = BackupHealthPresenter.PopupContent(result, Now);

        Assert.Contains("failed", title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub", body);
    }

    [Fact]
    public void PopupContentSaysNeverRunForANeverRunState()
    {
        var result = new BackupHealthResult(BackupHealthState.NeverRun, new[] { NeverRun("Google Drive") });
        var (title, body) = BackupHealthPresenter.PopupContent(result, Now);

        Assert.Contains("never run", title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never succeeded", body);
    }

    // --- ShouldNotify ----------------------------------------------------------

    // Repeated polls in the same state must not re-fire.
    [Theory]
    [InlineData(BackupHealthState.Failed)]
    [InlineData(BackupHealthState.Stale)]
    [InlineData(BackupHealthState.NeverRun)]
    public void ShouldNotNotifyOnRepeatedPollsInTheSameWarrantingState(BackupHealthState state)
    {
        Assert.False(BackupHealthPresenter.ShouldNotify(state, state));
    }

    [Fact]
    public void ShouldNotNotifyOnRepeatedHealthyPolls()
    {
        Assert.False(BackupHealthPresenter.ShouldNotify(BackupHealthState.Healthy, BackupHealthState.Healthy));
    }

    // A genuine transition into a problem state notifies.
    [Theory]
    [InlineData(BackupHealthState.Healthy, BackupHealthState.Failed)]
    [InlineData(BackupHealthState.Healthy, BackupHealthState.Stale)]
    [InlineData(BackupHealthState.NotConfigured, BackupHealthState.NeverRun)]
    public void ShouldNotifyOnATransitionIntoAWarrantingState(BackupHealthState previous, BackupHealthState current)
    {
        Assert.True(BackupHealthPresenter.ShouldNotify(previous, current));
    }

    // Null previous state (fresh install / first-ever poll, no persisted
    // dedupe yet) behaves like "coming from a quiet state" - a first poll
    // that already lands on NeverRun still notifies once.
    [Fact]
    public void NullPreviousStateStillNotifiesOnAWarrantingFirstPoll()
    {
        Assert.True(BackupHealthPresenter.ShouldNotify(null, BackupHealthState.NeverRun));
    }

    [Fact]
    public void NullPreviousStateDoesNotNotifyWhenTheFirstPollIsHealthy()
    {
        Assert.False(BackupHealthPresenter.ShouldNotify(null, BackupHealthState.Healthy));
    }

    // Recovery to Healthy gets no popup - just the badge clearing.
    [Fact]
    public void RecoveryToHealthyDoesNotNotify()
    {
        Assert.False(BackupHealthPresenter.ShouldNotify(BackupHealthState.Failed, BackupHealthState.Healthy));
    }

    // A second, different failure right after another warranting state (no
    // recovery in between) must NOT re-fire - only a transition FROM a
    // non-warranting state counts, per the design spec's exact wording.
    [Fact]
    public void ShouldNotNotifyWhenMovingBetweenTwoWarrantingStatesWithNoRecoveryBetween()
    {
        Assert.False(BackupHealthPresenter.ShouldNotify(BackupHealthState.Stale, BackupHealthState.Failed));
    }

    // The dedupe-survives-restart property: a persisted previous state of
    // Failed (as if freshly loaded from AppSettings after a restart) must
    // still suppress a re-notify for the same ongoing failure.
    [Fact]
    public void PersistedFailedStateSurvivingARestartSuppressesARepeatNotify()
    {
        BackupHealthState? persistedAcrossRestart = BackupHealthState.Failed;
        Assert.False(BackupHealthPresenter.ShouldNotify(persistedAcrossRestart, BackupHealthState.Failed));
    }
}

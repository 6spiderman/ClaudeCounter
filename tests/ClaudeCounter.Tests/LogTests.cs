// tests/ClaudeCounter.Tests/LogTests.cs
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// I4: Log.WithRetry is what keeps a concurrent writer's sharing violation
/// (the tray and the worker can now both touch the log file) from silently
/// dropping a line. Exercised directly via InternalsVisibleTo rather than by
/// taking a real OS-level exclusive lock on Log.FilePath - that path is the
/// one real, shared log file the whole test assembly reads and writes
/// concurrently (see BackupRunnerTests.ReadLog, OAuthConfigTests.ReadLog),
/// so a test that locks it exclusively would itself become the concurrent-
/// writer hazard I4 exists to fix, and would make every other test that
/// touches the log flaky whenever xunit happens to schedule it in parallel.
/// </summary>
public class LogTests
{
    [Fact]
    public void WithRetrySucceedsAfterTransientIOExceptions()
    {
        var attempts = 0;
        Log.WithRetry(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new IOException("simulated sharing violation");
        });
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void WithRetryGivesUpAfterExhaustingRetriesAndThrows()
    {
        var attempts = 0;
        Assert.Throws<IOException>(() => Log.WithRetry(() =>
        {
            attempts++;
            throw new IOException("simulated permanent sharing violation");
        }));
        // Retries a bounded number of times rather than forever - a few
        // attempts, not one and not hundreds.
        Assert.InRange(attempts, 2, 10);
    }

    [Fact]
    public void WithRetryDoesNotRetryNonIOExceptions()
    {
        var attempts = 0;
        Assert.Throws<InvalidOperationException>(() => Log.WithRetry(() =>
        {
            attempts++;
            throw new InvalidOperationException("not a sharing violation");
        }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void WithRetrySucceedsImmediatelyWithNoException()
    {
        var attempts = 0;
        Log.WithRetry(() => attempts++);
        Assert.Equal(1, attempts);
    }
}

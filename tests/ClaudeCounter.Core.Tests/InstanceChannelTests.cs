using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Tests.Fakes;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// InstanceChannel - the second-launch / --refresh channel. Every test uses
/// its own pipe name, so it never talks to a real running ClaudeCounter.
/// </summary>
public class InstanceChannelTests
{
    private static string UniqueName() => $"ClaudeCounterTest.{Guid.NewGuid():N}";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AShowCommandReachesTheRunningInstance()
    {
        var name = UniqueName();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = InstanceChannel.StartServer(c => received.TrySetResult(c), name);

        Assert.True(InstanceChannel.TrySend(InstanceChannel.Show, Timeout, name));
        Assert.Equal(InstanceChannel.Show, await received.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task TheServerKeepsListeningAfterACommand()
    {
        var name = UniqueName();
        var commands = new List<string>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = InstanceChannel.StartServer(c =>
        {
            lock (commands)
            {
                commands.Add(c);
                if (commands.Count == 2)
                    second.TrySetResult();
            }
        }, name);

        Assert.True(InstanceChannel.TrySend(InstanceChannel.Show, Timeout, name));
        Assert.True(InstanceChannel.TrySend(InstanceChannel.Refresh, Timeout, name));
        await second.Task.WaitAsync(Timeout);
        lock (commands)
            Assert.Equal(new[] { InstanceChannel.Show, InstanceChannel.Refresh }, commands);
    }

    [Fact]
    public void WithNothingListeningSendingFailsQuietly() =>
        Assert.False(InstanceChannel.TrySend(InstanceChannel.Show, TimeSpan.FromMilliseconds(300), UniqueName()));

    [Fact]
    public void PingAnswersWithoutReachingTheHandler()
    {
        var name = UniqueName();
        var handled = 0;
        using var server = InstanceChannel.StartServer(_ => Interlocked.Increment(ref handled), name);

        Assert.True(InstanceChannel.IsRunning(name));
        Assert.False(InstanceChannel.IsRunning(UniqueName()));
        Assert.Equal(0, Volatile.Read(ref handled));
    }

    [Fact]
    public void AnUnknownCommandIsRefusedAndNotHandled()
    {
        var name = UniqueName();
        var handled = 0;
        using var server = InstanceChannel.StartServer(_ => Interlocked.Increment(ref handled), name);

        Assert.False(InstanceChannel.TrySend("rm -rf", Timeout, name));
        Assert.True(InstanceChannel.IsRunning(name)); // still listening
        Assert.Equal(0, Volatile.Read(ref handled));
    }

    [Fact]
    public void AThrowingHandlerDoesNotStopTheListener()
    {
        var name = UniqueName();
        using var server = InstanceChannel.StartServer(_ => throw new InvalidOperationException("boom"), name);

        Assert.True(InstanceChannel.TrySend(InstanceChannel.Show, Timeout, name));
        Assert.True(InstanceChannel.IsRunning(name));
    }

    [Fact]
    public void OnWindowsTheNameIsPerUserAndSession()
    {
        var name = InstanceChannel.BuildDefaultName(isWindows: true, @"DOMAIN\Jo Smith", 3, runtimeDir: "/ignored");
        Assert.Equal("ClaudeCounter.DOMAIN_Jo_Smith.3", name);
    }

    [Fact]
    public void OnLinuxTheSocketGoesInTheRuntimeDirectory() =>
        Assert.Equal("/run/user/1000/ClaudeCounter.sock",
            InstanceChannel.BuildDefaultName(isWindows: false, "jo", 0, "/run/user/1000/"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/dir")]
    public void WithoutAUsableRuntimeDirectoryAPerUserNameIsUsed(string? runtimeDir) =>
        Assert.Equal("ClaudeCounter.jo_x", InstanceChannel.BuildDefaultName(isWindows: false, "jo.x", 0, runtimeDir));

    [Fact]
    public void ARuntimeDirectoryTooLongForASocketIsNotUsed()
    {
        var longDir = "/" + new string('d', InstanceChannel.MaxSocketPath);
        Assert.Equal("ClaudeCounter.jo", InstanceChannel.BuildDefaultName(isWindows: false, "jo", 0, longDir));
    }

    [Fact]
    public void ThisMachinesDefaultNameIsUsable()
    {
        var name = InstanceChannel.DefaultPipeName;
        Assert.Contains("ClaudeCounter", name, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', name);
    }

    [UnixOnlyFact]
    public void ASocketPathThePlatformRejectsMeansNotRunningRatherThanACrash()
    {
        var tooLong = "/tmp/" + new string('x', 200);
        Assert.False(InstanceChannel.TrySend(InstanceChannel.Ping, TimeSpan.FromMilliseconds(300), tooLong));
    }
}

/// <summary>PollingService.ResetForSignOut - the previous account's numbers vanish at once.</summary>
public class PollingServiceSignOutTests
{
    [Fact]
    public void ResetForSignOutClearsTheLastSnapshotAndNotifies()
    {
        using var client = new UsageClient();
        using var polling = new PollingService(
            new TokenProvider(new InMemorySessionStore(), new FakeRefresher(new TokenResult.Transient("unused"))),
            client, () => 5);
        PollState? seen = null;
        polling.Updated += s => seen = s;

        // Capture the live state object, then give it a previous account's data.
        polling.ResetForSignOut();
        var state = Assert.IsType<PollState>(seen);
        state.Snapshot = new UsageSnapshot(new UsageWindow(42, DateTimeOffset.UtcNow.AddHours(1)), null, null, null, null);
        state.LastSuccessAt = DateTimeOffset.Now;
        state.Problem = ProblemKind.TokenExpired;
        state.ProblemMessage = "expired";
        state.Source = TokenSource.OwnSession;
        seen = null;

        polling.ResetForSignOut();

        Assert.Same(state, seen);
        Assert.Null(state.Snapshot);
        Assert.Null(state.LastSuccessAt);
        Assert.Equal(ProblemKind.None, state.Problem);
        Assert.Null(state.ProblemMessage);
        Assert.Null(state.Source);
    }
}

using System.Security.Cryptography;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Tests.Fakes;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

[Collection("EnvironmentVariable")]
public class TokenProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("cc-provider");
    private readonly string? _originalEnv =
        Environment.GetEnvironmentVariable(TokenProvider.EnvVarName);

    public TokenProviderTests() =>
        Environment.SetEnvironmentVariable(TokenProvider.EnvVarName, null);

    private static OAuthSession Session(DateTimeOffset expiresAt, string access = "session-access") =>
        new(access, "session-refresh", expiresAt, "user:profile", Now.AddHours(-1));

    private string WriteCliFile(string json)
    {
        var path = Path.Combine(_dir.FullName, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string CliFileWithExpiry(DateTimeOffset? expiresAt, string access = "cli-access")
    {
        var expiry = expiresAt is { } at ? $",\"expiresAt\":{at.ToUnixTimeMilliseconds()}" : "";
        return WriteCliFile(
            $"{{\"claudeAiOauth\":{{\"accessToken\":\"{access}\",\"refreshToken\":\"cli-refresh\"{expiry}}}}}");
    }

    private TokenProvider Provider(
        ISessionStore session, ITokenRefresher refresher, string? cliPath = null) =>
        new(session, refresher,
            new CliCredentialsFile(cliPath ?? Path.Combine(_dir.FullName, "absent.json")),
            () => Now);

    // ---- tier 1: environment variable ---------------------------------------

    [Fact]
    public async Task EnvironmentVariableWinsOverEverything()
    {
        Environment.SetEnvironmentVariable(TokenProvider.EnvVarName, "  env-token  ");
        var session = new InMemorySessionStore(Session(Now.AddHours(3)));
        var refresher = FakeRefresher.Succeeding();

        var result = await Provider(session, refresher).GetTokenAsync(default);

        var token = Assert.IsType<CredentialResult.Token>(result);
        Assert.Equal("env-token", token.AccessToken);
        Assert.Equal(TokenSource.EnvironmentVariable, token.Source);
        Assert.Equal(0, refresher.Calls);
    }

    [Fact]
    public async Task BlankEnvironmentVariableIsIgnored()
    {
        Environment.SetEnvironmentVariable(TokenProvider.EnvVarName, "   ");
        var session = new InMemorySessionStore(Session(Now.AddHours(3)));

        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(session, FakeRefresher.Succeeding()).GetTokenAsync(default));

        Assert.Equal(TokenSource.OwnSession, token.Source);
    }

    // ---- tier 2: our own session --------------------------------------------

    [Fact]
    public async Task ValidSessionIsUsedWithoutRefreshing()
    {
        var refresher = FakeRefresher.Succeeding();
        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(new InMemorySessionStore(Session(Now.AddHours(3))), refresher)
                .GetTokenAsync(default));

        Assert.Equal("session-access", token.AccessToken);
        Assert.Equal(TokenSource.OwnSession, token.Source);
        Assert.Equal(0, refresher.Calls);
    }

    [Theory]
    [InlineData(-60)]   // already expired
    [InlineData(0)]     // expiring exactly now
    [InlineData(1)]     // inside the two-minute grace window
    public async Task SessionAtOrNearExpiryIsRefreshed(int minutesFromNow)
    {
        var store = new InMemorySessionStore(Session(Now.AddMinutes(minutesFromNow)));
        var refresher = FakeRefresher.Succeeding();

        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(store, refresher).GetTokenAsync(default));

        Assert.Equal(1, refresher.Calls);
        Assert.Equal("session-refresh", refresher.LastRefreshToken);
        Assert.Equal("fresh-access", token.AccessToken);
    }

    [Fact]
    public async Task RefreshedTokensArePersistedBeforeUse()
    {
        // The server invalidates the old refresh token the instant it issues a
        // new one. Losing the rotated token here means a forced re-login.
        var store = new InMemorySessionStore(Session(Now.AddMinutes(-1)));

        await Provider(store, FakeRefresher.Succeeding()).GetTokenAsync(default);

        Assert.Equal(1, store.Writes);
        Assert.Equal("rotated-refresh", store.Read()!.RefreshToken);
        Assert.Equal("fresh-access", store.Read()!.AccessToken);
    }

    [Fact]
    public async Task RejectedSessionIsClearedAndFallsThroughToBootstrap()
    {
        var store = new InMemorySessionStore(Session(Now.AddMinutes(-1)));
        var cli = CliFileWithExpiry(Now.AddHours(2));

        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(store, FakeRefresher.Rejecting(), cli).GetTokenAsync(default));

        Assert.Equal(1, store.Clears);
        Assert.Null(store.Read());
        Assert.Equal(TokenSource.CliBootstrap, token.Source);
        Assert.Equal("cli-access", token.AccessToken);
    }

    [Fact]
    public async Task RejectedSessionWithNoCliFileAsksForSignIn()
    {
        var store = new InMemorySessionStore(Session(Now.AddMinutes(-1)));
        Assert.IsType<CredentialResult.SignInRequired>(
            await Provider(store, FakeRefresher.Rejecting()).GetTokenAsync(default));
    }

    [Fact]
    public async Task TransientRefreshFailureDoesNotFallThroughToBootstrap()
    {
        // Falling back here would paper over a network problem with a stale
        // token and make the real fault much harder to see.
        var store = new InMemorySessionStore(Session(Now.AddMinutes(-1)));
        var cli = CliFileWithExpiry(Now.AddHours(2));

        var result = await Provider(store, FakeRefresher.Failing(), cli).GetTokenAsync(default);

        Assert.IsType<CredentialResult.TransientError>(result);
        Assert.Equal(0, store.Clears);
        Assert.NotNull(store.Read());
    }

    // ---- CC-04: cross-session refresh lock -----------------------------------

    [Fact]
    public async Task AnotherProcesssRefreshDuringTheLockWaitIsUsedInstead()
    {
        // Simulates a second ClaudeCounter process (a concurrent Windows
        // session) winning the refresh race: by the time this process gets
        // the lock and re-reads, the session on disk is already fresh.
        var expired = Session(Now.AddMinutes(-1));
        var refreshedElsewhere = Session(Now.AddHours(4), access: "refreshed-elsewhere");
        var store = new SequencedSessionStore(expired, refreshedElsewhere);
        var refresher = FakeRefresher.Succeeding();

        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(store, refresher).GetTokenAsync(default));

        Assert.Equal(0, refresher.Calls);
        Assert.Equal("refreshed-elsewhere", token.AccessToken);
        Assert.Equal(TokenSource.OwnSession, token.Source);
        Assert.Equal(2, store.ReadCount);
    }

    [Fact]
    public async Task StillExpiredAfterTheLockRefreshesExactlyOnce()
    {
        // The ordinary case: nobody else touched the session while we waited,
        // so we still have to refresh - but only once, not once per read.
        var store = new SequencedSessionStore(Session(Now.AddMinutes(-1)), Session(Now.AddMinutes(-1)));
        var refresher = FakeRefresher.Succeeding();

        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(store, refresher).GetTokenAsync(default));

        Assert.Equal(1, refresher.Calls);
        Assert.Equal("fresh-access", token.AccessToken);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task SessionClearedByAnotherProcessDuringTheLockFallsThroughToBootstrap()
    {
        // The other process rejected the session and cleared it before we got
        // the lock; we must not treat "session vanished mid-refresh" as a bug.
        var store = new SequencedSessionStore(Session(Now.AddMinutes(-1)), null);
        var cli = CliFileWithExpiry(Now.AddHours(2));
        var refresher = FakeRefresher.Succeeding();

        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(store, refresher, cli).GetTokenAsync(default));

        Assert.Equal(0, refresher.Calls);
        Assert.Equal(TokenSource.CliBootstrap, token.Source);
    }

    // ---- tier 3: Claude Code bootstrap --------------------------------------

    [Fact]
    public async Task ValidCliTokenIsBorrowedWhenWeHaveNoSession()
    {
        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(new InMemorySessionStore(), FakeRefresher.Succeeding(),
                CliFileWithExpiry(Now.AddHours(2))).GetTokenAsync(default));

        Assert.Equal("cli-access", token.AccessToken);
        Assert.Equal(TokenSource.CliBootstrap, token.Source);
    }

    [Fact]
    public async Task CliTokenWithNoExpiryIsBorrowed()
    {
        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(new InMemorySessionStore(), FakeRefresher.Succeeding(),
                CliFileWithExpiry(null)).GetTokenAsync(default));

        Assert.Equal(TokenSource.CliBootstrap, token.Source);
    }

    [Fact]
    public async Task ExpiredCliTokenIsNeverRefreshed()
    {
        // THE regression guard. Refreshing Claude Code's chain rotates its
        // refresh token, and the CLI's copy dies the moment it next refreshes.
        var refresher = FakeRefresher.Succeeding();

        var result = await Provider(new InMemorySessionStore(), refresher,
            CliFileWithExpiry(Now.AddMinutes(-1))).GetTokenAsync(default);

        Assert.Equal(0, refresher.Calls);
        Assert.IsType<CredentialResult.SignInRequired>(result);
    }

    [Fact]
    public async Task CliCredentialsFileIsByteIdenticalAfterAFullCycle()
    {
        // The user-visible promise: run ClaudeCounter all day, including a
        // session refresh, and Claude Code's file is untouched.
        var cli = CliFileWithExpiry(Now.AddHours(2));
        var before = SHA256.HashData(File.ReadAllBytes(cli));

        var store = new InMemorySessionStore(Session(Now.AddMinutes(-1)));
        var provider = Provider(store, FakeRefresher.Succeeding(), cli);

        for (var i = 0; i < 3; i++)
            await provider.GetTokenAsync(default);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(cli)));
    }

    [Fact]
    public async Task MissingCliFileAsksForSignIn()
    {
        var result = await Provider(new InMemorySessionStore(), FakeRefresher.Succeeding())
            .GetTokenAsync(default);

        var required = Assert.IsType<CredentialResult.SignInRequired>(result);
        Assert.Equal("Not signed in", required.Reason);
    }

    [Fact]
    public async Task MalformedCliFileAsksForSignInWithoutThrowing()
    {
        var result = await Provider(new InMemorySessionStore(), FakeRefresher.Succeeding(),
            WriteCliFile("{ not json")).GetTokenAsync(default);

        var required = Assert.IsType<CredentialResult.SignInRequired>(result);
        Assert.Contains("unreadable", required.Reason);
    }

    [Fact]
    public async Task OwnSessionIsPreferredOverTheCliFile()
    {
        var store = new InMemorySessionStore(Session(Now.AddHours(3)));

        var token = Assert.IsType<CredentialResult.Token>(
            await Provider(store, FakeRefresher.Succeeding(), CliFileWithExpiry(Now.AddHours(3)))
                .GetTokenAsync(default));

        Assert.Equal(TokenSource.OwnSession, token.Source);
        Assert.Equal("session-access", token.AccessToken);
    }

    [Fact]
    public void HasOwnSessionReflectsTheStore()
    {
        Assert.False(Provider(new InMemorySessionStore(), FakeRefresher.Succeeding()).HasOwnSession);
        Assert.True(Provider(new InMemorySessionStore(Session(Now.AddHours(3))),
            FakeRefresher.Succeeding()).HasOwnSession);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenProvider.EnvVarName, _originalEnv);
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}

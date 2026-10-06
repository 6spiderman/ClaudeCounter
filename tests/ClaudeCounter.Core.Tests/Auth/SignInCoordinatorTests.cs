using ClaudeCounter.Core.Auth;
using ClaudeCounter.Tests.Fakes;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

public class SignInCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private static SignInCoordinator Coordinator(
        IAuthCodeExchanger exchanger, ISessionStore store) =>
        new(exchanger, store, () => Now);

    [Fact]
    public async Task HappyPathStoresASessionDerivedFromTheResponse()
    {
        var expiresAt = Now.AddHours(8);
        var store = new InMemorySessionStore();
        var coordinator = Coordinator(
            FakeExchanger.Succeeding(expiresAt.ToUnixTimeMilliseconds()), store);

        var request = coordinator.Begin();
        var outcome = await coordinator.CompleteAsync($"the-code#{request.State}", default);

        var success = Assert.IsType<SignInOutcome.Success>(outcome);
        Assert.Equal("new-access", success.Session.AccessToken);
        Assert.Equal("new-refresh", success.Session.RefreshToken);
        Assert.Equal(expiresAt, success.Session.ExpiresAt);
        Assert.Equal(Now, success.Session.ObtainedAt);
        Assert.Equal(1, store.Writes);
        Assert.Equal(success.Session, store.Read());
    }

    [Fact]
    public async Task TheVerifierSentMatchesTheChallengeInTheUrl()
    {
        var exchanger = FakeExchanger.Succeeding();
        var coordinator = Coordinator(exchanger, new InMemorySessionStore());

        var request = coordinator.Begin();
        await coordinator.CompleteAsync($"code#{request.State}", default);

        Assert.Equal(request.CodeVerifier, exchanger.LastVerifier);
        Assert.Equal(request.State, exchanger.LastState);
        Assert.Equal("code", exchanger.LastCode);

        // The whole point of PKCE: the challenge travels, the verifier does not.
        var challenge = PkceCodes.FromVerifier(request.CodeVerifier).Challenge;
        Assert.Contains($"code_challenge={challenge}", request.Url);
    }

    [Fact]
    public async Task CompleteBeforeBeginIsRejected()
    {
        var exchanger = FakeExchanger.Succeeding();
        var outcome = await Coordinator(exchanger, new InMemorySessionStore())
            .CompleteAsync("anything", default);

        Assert.IsType<SignInOutcome.NotStarted>(outcome);
        Assert.Equal(0, exchanger.Calls);
    }

    [Fact]
    public async Task StateMismatchIsRefusedWithoutCallingTheServer()
    {
        var exchanger = FakeExchanger.Succeeding();
        var store = new InMemorySessionStore();
        var coordinator = Coordinator(exchanger, store);

        coordinator.Begin();
        var outcome = await coordinator.CompleteAsync("code#someone-elses-state", default);

        Assert.IsType<SignInOutcome.StateMismatch>(outcome);
        Assert.Equal(0, exchanger.Calls);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task CodeWithNoStateIsAccepted()
    {
        // Anthropic shows "code#state", but a user who copies only the code
        // should not be stonewalled - the PKCE verifier still protects us.
        var coordinator = Coordinator(FakeExchanger.Succeeding(), new InMemorySessionStore());
        coordinator.Begin();

        Assert.IsType<SignInOutcome.Success>(await coordinator.CompleteAsync("bare-code", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#only-state")]
    public async Task UnparseableInputIsBadInput(string pasted)
    {
        var exchanger = FakeExchanger.Succeeding();
        var coordinator = Coordinator(exchanger, new InMemorySessionStore());
        coordinator.Begin();

        Assert.IsType<SignInOutcome.BadInput>(await coordinator.CompleteAsync(pasted, default));
        Assert.Equal(0, exchanger.Calls);
    }

    [Fact]
    public async Task RejectedCodeStoresNothingAndCannotBeRetried()
    {
        var store = new InMemorySessionStore();
        var coordinator = Coordinator(FakeExchanger.Rejecting(), store);
        var request = coordinator.Begin();

        Assert.IsType<SignInOutcome.Rejected>(
            await coordinator.CompleteAsync($"code#{request.State}", default));
        Assert.Equal(0, store.Writes);

        // The attempt is spent: codes are single-use, so a second try with the
        // same paste must not reach the server again.
        Assert.IsType<SignInOutcome.NotStarted>(
            await coordinator.CompleteAsync($"code#{request.State}", default));
    }

    [Fact]
    public async Task TransientFailureKeepsTheAttemptRetryable()
    {
        var exchanger = FakeExchanger.Failing();
        var coordinator = Coordinator(exchanger, new InMemorySessionStore());
        var request = coordinator.Begin();

        Assert.IsType<SignInOutcome.Transient>(
            await coordinator.CompleteAsync($"code#{request.State}", default));

        // The code may never have been consumed, so retrying is worth allowing.
        Assert.IsType<SignInOutcome.Transient>(
            await coordinator.CompleteAsync($"code#{request.State}", default));
        Assert.Equal(2, exchanger.Calls);
    }

    [Fact]
    public async Task BeginningAgainInvalidatesTheFirstAttempt()
    {
        var coordinator = Coordinator(FakeExchanger.Succeeding(), new InMemorySessionStore());

        var first = coordinator.Begin();
        var second = coordinator.Begin();
        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);

        // A code from the abandoned attempt no longer matches.
        Assert.IsType<SignInOutcome.StateMismatch>(
            await coordinator.CompleteAsync($"code#{first.State}", default));
    }

    [Fact]
    public async Task ResetAbandonsTheAttempt()
    {
        var coordinator = Coordinator(FakeExchanger.Succeeding(), new InMemorySessionStore());
        var request = coordinator.Begin();
        coordinator.Reset();

        Assert.IsType<SignInOutcome.NotStarted>(
            await coordinator.CompleteAsync($"code#{request.State}", default));
    }

    [Fact]
    public async Task AcceptsAFullCallbackUrlPastedFromTheAddressBar()
    {
        var coordinator = Coordinator(FakeExchanger.Succeeding(), new InMemorySessionStore());
        var request = coordinator.Begin();

        var pasted = "https://console.anthropic.com/oauth/code/callback" +
                     $"?code=abc123&state={Uri.EscapeDataString(request.State)}";

        Assert.IsType<SignInOutcome.Success>(await coordinator.CompleteAsync(pasted, default));
    }
}

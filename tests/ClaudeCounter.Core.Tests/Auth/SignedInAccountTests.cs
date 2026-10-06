using System.Net;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Tests.Fakes;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

/// <summary>
/// "Signed in as ..." in the tray menu: the account email is picked up from
/// the token response, kept across refreshes and saved with the session, and
/// SignInMenu turns it into the menu entry.
/// </summary>
public class SignedInAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TokenResponseAccountEmailIsCaptured()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","expires_in":3600,"account":{"uuid":"u1","email_address":"me@example.com"}}""");

        var success = Assert.IsType<TokenResult.Success>(
            await new OAuthTokenRefresher(handler, () => Now).RefreshAsync("old", default));

        Assert.Equal("me@example.com", success.Tokens.AccountEmail);
    }

    [Theory]
    [InlineData("""{"access_token":"a","refresh_token":"r"}""")]
    [InlineData("""{"access_token":"a","refresh_token":"r","account":"not-an-object"}""")]
    [InlineData("""{"access_token":"a","refresh_token":"r","account":{"uuid":"u1"}}""")]
    public async Task AMissingOrOddAccountIsNotAnError(string body)
    {
        var success = Assert.IsType<TokenResult.Success>(
            await new OAuthTokenRefresher(new StubHandler(HttpStatusCode.OK, body), () => Now).RefreshAsync("old", default));

        Assert.Null(success.Tokens.AccountEmail);
    }

    [Fact]
    public void ARefreshThatOmitsTheAccountKeepsTheKnownEmail()
    {
        var tokens = new OAuthTokens("a", "r", Now.AddHours(1).ToUnixTimeMilliseconds(), null);
        Assert.Equal("me@example.com", OAuthSession.FromTokens(tokens, Now, "me@example.com").AccountEmail);

        var named = tokens with { AccountEmail = "new@example.com" };
        Assert.Equal("new@example.com", OAuthSession.FromTokens(named, Now, "me@example.com").AccountEmail);
    }

    [Fact]
    public void EmailIsSavedWithTheSessionAndOldSessionsStillLoad()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "session.dat");
            var store = new EncryptedSessionStore(path, new PassthroughProtector());
            var session = new OAuthSession("a", "r", Now.AddHours(1), null, Now) { AccountEmail = "me@example.com" };

            store.Write(session);
            Assert.Equal("me@example.com", store.Read()!.AccountEmail);

            store.Write(session with { AccountEmail = null }); // a session saved before the field existed
            Assert.Null(store.Read()!.AccountEmail);
            Assert.Equal("a", store.Read()!.AccessToken);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void OwnSessionShowsWhoIsSignedIn()
    {
        Assert.Equal(("Signed in as me@example.com", false, true, true), SignInMenu.For(TokenSource.OwnSession, false, "me@example.com"));
        Assert.Equal(("Signed in to Claude", false, true, true), SignInMenu.For(TokenSource.OwnSession, false, null));
    }

    [Fact]
    public void OtherSourcesKeepOfferingSignIn()
    {
        Assert.Equal(("Sign in to Claude... (using Claude Code's session)", true, false, false), SignInMenu.For(TokenSource.CliBootstrap, false, null));
        Assert.Equal(("Sign in to Claude...", true, false, false), SignInMenu.For(null, false, null));
        Assert.False(SignInMenu.For(TokenSource.EnvironmentVariable, false, null).Enabled);
    }

    [Fact]
    public void AnExpiredOwnSessionAsksToSignInAgain() =>
        Assert.Equal(("Sign in to Claude...", true, false, false), SignInMenu.For(TokenSource.OwnSession, true, "me@example.com"));

    [Fact]
    public void SignOutIsOfferedOnlyForClaudeCountersOwnSession()
    {
        Assert.True(SignInMenu.For(TokenSource.OwnSession, false, null).ShowSignOut);
        Assert.False(SignInMenu.For(TokenSource.CliBootstrap, false, null).ShowSignOut);
        Assert.False(SignInMenu.For(TokenSource.EnvironmentVariable, false, null).ShowSignOut);
        Assert.False(SignInMenu.For(null, false, null).ShowSignOut);
        // An expired own session asks to sign in again; nothing left to sign out of.
        Assert.False(SignInMenu.For(TokenSource.OwnSession, true, "me@example.com").ShowSignOut);
    }

    private sealed class PassthroughProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[]? Unprotect(byte[] ciphertext) => ciphertext;
    }
}

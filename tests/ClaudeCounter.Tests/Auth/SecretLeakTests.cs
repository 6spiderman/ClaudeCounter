using System.Net;
using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Tests.Fakes;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

/// <summary>
/// Nothing secret may reach the log. The sign-in path handles a PKCE verifier,
/// an authorization code, an access token and a refresh token, and users are
/// asked to paste log excerpts into bug reports.
/// </summary>
public class SecretLeakTests
{
    private const string Access = "sk-ant-oat01-ACCESSTOKENVALUE";
    private const string Refresh = "sk-ant-ort01-REFRESHTOKENVALUE";
    private const string Code = "AUTHORIZATIONCODEVALUE";

    private static string ReadLog()
    {
        if (Log.FilePath is not { } path || !File.Exists(path))
            return string.Empty;

        // Opened share-all: the logger holds it while other tests run.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertNoSecretsIn(string log, params string[] secrets)
    {
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulSignInLogsNoSecrets()
    {
        var exchanger = new FakeExchanger(new TokenResult.Success(
            new OAuthTokens(Access, Refresh, DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds(), null)));

        var coordinator = new SignInCoordinator(exchanger, new InMemorySessionStore());
        var request = coordinator.Begin();
        var before = ReadLog().Length;

        await coordinator.CompleteAsync($"{Code}#{request.State}", default);

        var written = ReadLog()[before..];
        AssertNoSecretsIn(written, Access, Refresh, Code, request.CodeVerifier, request.State);
    }

    [Fact]
    public async Task ARejectedSignInLogsNoSecrets()
    {
        var coordinator = new SignInCoordinator(FakeExchanger.Rejecting(), new InMemorySessionStore());
        var request = coordinator.Begin();
        var before = ReadLog().Length;

        await coordinator.CompleteAsync($"{Code}#{request.State}", default);

        var written = ReadLog()[before..];
        AssertNoSecretsIn(written, Code, request.CodeVerifier, request.State);
    }

    [Fact]
    public async Task ARejectedRefreshLogsNoSecrets()
    {
        // The token endpoint echoes error bodies, and a server can include the
        // credential it just refused in one.
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"refresh token {{Refresh}} revoked"}""");

        var store = new InMemorySessionStore(new OAuthSession(
            Access, Refresh, DateTimeOffset.UtcNow.AddMinutes(-5), null, DateTimeOffset.UtcNow.AddHours(-9)));

        var provider = new TokenProvider(
            store,
            new OAuthTokenRefresher(handler),
            new CliCredentialsFile(Path.Combine(Path.GetTempPath(), "cc-absent-creds.json")));

        var before = ReadLog().Length;
        await provider.GetTokenAsync(default);

        var written = ReadLog()[before..];
        AssertNoSecretsIn(written, Access, Refresh);
    }

    [Fact]
    public void TheAuthorizeUrlNeverContainsTheVerifier()
    {
        // The URL is shown in the UI and can be copied to the clipboard, so the
        // verifier leaking into it would defeat PKCE entirely.
        var request = AuthorizeRequest.Create();
        Assert.DoesNotContain(request.CodeVerifier, request.Url, StringComparison.Ordinal);
    }
}

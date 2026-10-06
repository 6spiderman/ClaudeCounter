using System.Web;
using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

public class AuthorizeRequestTests
{
    private static Dictionary<string, string> QueryOf(string url)
    {
        var uri = new Uri(url);
        var parsed = HttpUtility.ParseQueryString(uri.Query);
        return parsed.AllKeys
            .Where(k => k is not null)
            .ToDictionary(k => k!, k => parsed[k!] ?? string.Empty);
    }

    [Fact]
    public void PointsAtTheAuthorizeEndpoint()
    {
        var request = AuthorizeRequest.Create();
        Assert.StartsWith("https://claude.ai/oauth/authorize?", request.Url);
    }

    [Fact]
    public void CarriesEveryParameterTheFlowNeeds()
    {
        var codes = PkceCodes.FromVerifier("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
        var query = QueryOf(AuthorizeRequest.Create(codes, "the-state").Url);

        Assert.Equal("true", query["code"]);
        Assert.Equal(OAuthConfig.ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(OAuthConfig.RedirectUri, query["redirect_uri"]);
        Assert.Equal(OAuthConfig.Scopes, query["scope"]);
        Assert.Equal(codes.Challenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("the-state", query["state"]);
    }

    [Fact]
    public void ScopeSurvivesPercentEncoding()
    {
        // Spaces and colons in "user:profile user:inference" must round-trip,
        // or the server sees a different scope set.
        var query = QueryOf(AuthorizeRequest.Create().Url);
        Assert.Equal("user:profile user:inference", query["scope"]);
    }

    [Fact]
    public void NeverRequestsTheBroadOrgScope()
    {
        // CC-01: the request must not carry org:create_api_key, which would
        // let the stored token mint API keys rather than just read usage.
        var query = QueryOf(AuthorizeRequest.Create().Url);
        Assert.DoesNotContain("org:create_api_key", query["scope"]);
        Assert.Equal(OAuthConfig.Scopes, query["scope"]);
    }

    [Fact]
    public void RedirectUriSurvivesPercentEncoding()
    {
        var query = QueryOf(AuthorizeRequest.Create().Url);
        Assert.Equal("https://console.anthropic.com/oauth/code/callback", query["redirect_uri"]);
    }

    [Fact]
    public void SecretsAreReturnedAlongsideTheUrl()
    {
        var codes = PkceCodes.Generate();
        var request = AuthorizeRequest.Create(codes, "s");

        // The verifier must never appear in the URL - only the challenge does.
        Assert.Equal(codes.Verifier, request.CodeVerifier);
        Assert.Equal("s", request.State);
        Assert.DoesNotContain(codes.Verifier, request.Url);
    }

    [Fact]
    public void EachRequestGetsAFreshStateAndVerifier()
    {
        var a = AuthorizeRequest.Create();
        var b = AuthorizeRequest.Create();

        Assert.NotEqual(a.State, b.State);
        Assert.NotEqual(a.CodeVerifier, b.CodeVerifier);
    }
}

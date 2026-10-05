using System.Security.Cryptography;

namespace ClaudeCounter.Core.Auth;

/// <summary>
/// One authorization attempt: the URL to send the user to, plus the two secrets
/// that must survive until they paste the code back. Pure - building it makes
/// no network call.
/// </summary>
public sealed record AuthorizeRequest(string Url, string State, string CodeVerifier)
{
    public static AuthorizeRequest Create(PkceCodes? codes = null, string? state = null)
    {
        var pkce = codes ?? PkceCodes.Generate();
        var csrfState = state ?? Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

        var query = string.Join('&',
            // code=true asks for the copy/paste page rather than a redirect
            // back to a listener we do not run.
            "code=true",
            $"client_id={Uri.EscapeDataString(OAuthConfig.ClientId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(OAuthConfig.RedirectUri)}",
            $"scope={Uri.EscapeDataString(OAuthConfig.Scopes)}",
            $"code_challenge={pkce.Challenge}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(csrfState)}");

        return new AuthorizeRequest($"{OAuthConfig.Authorize}?{query}", csrfState, pkce.Verifier);
    }
}

namespace ClaudeCounter.Core.Auth;

public interface IAuthCodeExchanger
{
    Task<TokenResult> ExchangeAsync(string code, string state, string codeVerifier, CancellationToken ct);
}

/// <summary>
/// Redeems an authorization code for a token pair. Codes are single-use and
/// expire within minutes, so a <see cref="TokenResult.Rejected"/> here means
/// start the whole flow again - never retry the same code.
/// </summary>
public sealed class OAuthCodeExchanger : IAuthCodeExchanger, IDisposable
{
    private readonly OAuthTokenEndpoint _endpoint;
    private readonly bool _ownsEndpoint;

    public OAuthCodeExchanger(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
        : this(new OAuthTokenEndpoint(handler, now), ownsEndpoint: true)
    {
    }

    public OAuthCodeExchanger(OAuthTokenEndpoint endpoint, bool ownsEndpoint = false)
    {
        _endpoint = endpoint;
        _ownsEndpoint = ownsEndpoint;
    }

    public Task<TokenResult> ExchangeAsync(
        string code, string state, string codeVerifier, CancellationToken ct) =>
        _endpoint.PostAsync(
            new
            {
                grant_type = "authorization_code",
                code,
                state,
                client_id = OAuthConfig.ClientId,
                redirect_uri = OAuthConfig.RedirectUri,
                code_verifier = codeVerifier,
            },
            // There is nothing to fall back to: a code exchange that returns no
            // refresh token leaves us unable to stay signed in.
            fallbackRefreshToken: null,
            ct);

    public void Dispose()
    {
        if (_ownsEndpoint)
            _endpoint.Dispose();
    }
}

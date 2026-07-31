namespace ClaudeCounter.Core.Auth;

public interface ITokenRefresher
{
    Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct);
}

/// <summary>
/// Exchanges a refresh token for a fresh access token. The server rotates the
/// refresh token on every use and invalidates the old one immediately, so the
/// caller must persist the result before the next refresh - and only one holder
/// of a given chain may ever refresh it.
/// </summary>
public sealed class OAuthTokenRefresher : ITokenRefresher, IDisposable
{
    private readonly OAuthTokenEndpoint _endpoint;
    private readonly bool _ownsEndpoint;

    public OAuthTokenRefresher(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
        : this(new OAuthTokenEndpoint(handler, now), ownsEndpoint: true)
    {
    }

    public OAuthTokenRefresher(OAuthTokenEndpoint endpoint, bool ownsEndpoint = false)
    {
        _endpoint = endpoint;
        _ownsEndpoint = ownsEndpoint;
    }

    public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
        _endpoint.PostAsync(
            new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                client_id = OAuthConfig.ClientId,
            },
            // A refresh that does not rotate leaves us on the current token.
            fallbackRefreshToken: refreshToken,
            ct);

    public void Dispose()
    {
        if (_ownsEndpoint)
            _endpoint.Dispose();
    }
}

namespace ClaudeCounter.Core.Auth;

/// <summary>Tokens as returned by the authorization server.</summary>
public sealed record OAuthTokens(
    string AccessToken,
    string RefreshToken,
    long ExpiresAtUnixMs,
    string? Scopes)
{
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtUnixMs);
}

/// <summary>
/// Outcome of a call to the token endpoint. Shared by both grant types: an
/// authorization-code exchange and a refresh fail in exactly the same ways.
/// </summary>
public abstract record TokenResult
{
    public sealed record Success(OAuthTokens Tokens) : TokenResult;

    /// <summary>
    /// The server refused the grant: a dead refresh token, or a code that was
    /// already used or has expired. Retrying is pointless - the user must act.
    /// </summary>
    public sealed record Rejected(string Message) : TokenResult;

    /// <summary>Network blip, rate limit, or server error. Safe to retry later.</summary>
    public sealed record Transient(string Message) : TokenResult;
}

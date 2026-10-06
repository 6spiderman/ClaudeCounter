using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.Tests.Fakes;

/// <summary>
/// Records every refresh attempt. The call count matters as much as the result:
/// several tests assert that a given code path refreshes <em>nothing</em>.
/// </summary>
public sealed class FakeRefresher : ITokenRefresher
{
    private readonly TokenResult _result;

    public FakeRefresher(TokenResult result) => _result = result;

    public int Calls { get; private set; }
    public string? LastRefreshToken { get; private set; }

    public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        Calls++;
        LastRefreshToken = refreshToken;
        return Task.FromResult(_result);
    }

    public static FakeRefresher Succeeding(
        string access = "fresh-access",
        string refresh = "rotated-refresh",
        DateTimeOffset? expiresAt = null) =>
        new(new TokenResult.Success(new OAuthTokens(
            access,
            refresh,
            (expiresAt ?? DateTimeOffset.UtcNow.AddHours(8)).ToUnixTimeMilliseconds(),
            "user:profile")));

    public static FakeRefresher Rejecting() => new(new TokenResult.Rejected("HTTP 400 invalid_grant"));

    public static FakeRefresher Failing() => new(new TokenResult.Transient("network down"));
}

/// <summary>Canned <see cref="IAuthCodeExchanger"/> for sign-in flow tests.</summary>
public sealed class FakeExchanger : IAuthCodeExchanger
{
    private readonly TokenResult _result;

    public FakeExchanger(TokenResult result) => _result = result;

    public int Calls { get; private set; }
    public string? LastCode { get; private set; }
    public string? LastState { get; private set; }
    public string? LastVerifier { get; private set; }

    public Task<TokenResult> ExchangeAsync(
        string code, string state, string codeVerifier, CancellationToken ct)
    {
        Calls++;
        LastCode = code;
        LastState = state;
        LastVerifier = codeVerifier;
        return Task.FromResult(_result);
    }

    public static FakeExchanger Succeeding(long? expiresAtUnixMs = null) =>
        new(new TokenResult.Success(new OAuthTokens(
            "new-access",
            "new-refresh",
            expiresAtUnixMs ?? DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds(),
            "user:profile")));

    public static FakeExchanger Rejecting() => new(new TokenResult.Rejected("HTTP 400 invalid_grant"));

    public static FakeExchanger Failing() => new(new TokenResult.Transient("network down"));
}

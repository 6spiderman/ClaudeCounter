namespace ClaudeCounter.Core.Auth;

public abstract record SignInOutcome
{
    public sealed record Success(OAuthSession Session) : SignInOutcome;

    /// <summary>The pasted text was not a code in any recognizable shape.</summary>
    public sealed record BadInput(string Message) : SignInOutcome;

    /// <summary>The code came from a different attempt. Start over.</summary>
    public sealed record StateMismatch : SignInOutcome;

    /// <summary>The server refused the code - used already, or expired. Start over.</summary>
    public sealed record Rejected(string Message) : SignInOutcome;

    /// <summary>Could not reach the server.</summary>
    public sealed record Transient(string Message) : SignInOutcome;

    /// <summary>Complete was called before Begin.</summary>
    public sealed record NotStarted : SignInOutcome;
}

/// <summary>
/// Drives one sign-in from end to end without knowing anything about the UI:
/// <see cref="Begin"/> produces the URL to open, <see cref="CompleteAsync"/>
/// takes whatever the user pasted back. The view is left with nothing to do but
/// render the outcome, which is what makes every branch of the flow unit
/// testable with no browser involved.
/// </summary>
public sealed class SignInCoordinator
{
    private readonly IAuthCodeExchanger _exchanger;
    private readonly ISessionStore _store;
    private readonly Func<DateTimeOffset> _now;

    private AuthorizeRequest? _pending;

    public SignInCoordinator(
        IAuthCodeExchanger exchanger,
        ISessionStore store,
        Func<DateTimeOffset>? now = null)
    {
        _exchanger = exchanger;
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Starts an attempt. Each call mints a fresh PKCE verifier and state, so a
    /// code from an earlier attempt can no longer be redeemed - which is the
    /// point: codes are single-use and the user may have tried twice.
    /// </summary>
    public AuthorizeRequest Begin()
    {
        _pending = AuthorizeRequest.Create();
        return _pending;
    }

    public void Reset() => _pending = null;

    public async Task<SignInOutcome> CompleteAsync(string? pasted, CancellationToken ct)
    {
        if (_pending is not { } attempt)
            return new SignInOutcome.NotStarted();

        if (PastedCode.Parse(pasted) is not { } parsed)
            return new SignInOutcome.BadInput("That does not look like an authorization code.");

        // The server echoes our state back. A mismatch means the code belongs to
        // a different attempt - or to someone else's.
        if (parsed.State is { } returned && !FixedTimeEquals(returned, attempt.State))
            return new SignInOutcome.StateMismatch();

        var result = await _exchanger.ExchangeAsync(
            parsed.Code, attempt.State, attempt.CodeVerifier, ct);

        switch (result)
        {
            case TokenResult.Success success:
                var session = OAuthSession.FromTokens(success.Tokens, _now());
                _store.Write(session);
                _pending = null;   // the code is spent either way
                Log.Info("Signed in. ClaudeCounter now has its own session.");
                OAuthConfig.LogIfBroaderThanNeeded(success.Tokens.Scopes);
                return new SignInOutcome.Success(session);

            case TokenResult.Rejected rejected:
                _pending = null;   // this code can never work again
                Log.Warn($"Sign-in rejected: {rejected.Message}");
                return new SignInOutcome.Rejected(rejected.Message);

            case TokenResult.Transient transient:
                // Keep the attempt: the code may not have been consumed, so the
                // same paste can be retried once the network recovers.
                Log.Warn($"Sign-in could not reach Anthropic: {transient.Message}");
                return new SignInOutcome.Transient(transient.Message);

            default:
                return new SignInOutcome.Transient("Unexpected response");
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));
}

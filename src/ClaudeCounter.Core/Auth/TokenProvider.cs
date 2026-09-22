namespace ClaudeCounter.Core.Auth;

public enum TokenSource
{
    /// <summary>CLAUDE_CODE_OAUTH_TOKEN. Used verbatim, never refreshed.</summary>
    EnvironmentVariable,

    /// <summary>ClaudeCounter's own session. The normal case.</summary>
    OwnSession,

    /// <summary>Borrowed from Claude Code's file until the user signs in here.</summary>
    CliBootstrap,
}

public abstract record CredentialResult
{
    public sealed record Token(string AccessToken, TokenSource Source) : CredentialResult;

    /// <summary>No usable token. Only the user can fix this, so stop retrying hard.</summary>
    public sealed record SignInRequired(string Reason) : CredentialResult;

    /// <summary>Could not reach the server. Retry later.</summary>
    public sealed record TransientError(string Message) : CredentialResult;
}

/// <summary>
/// Resolves the access token used to read usage, in three tiers:
/// <list type="number">
///   <item>the <c>CLAUDE_CODE_OAUTH_TOKEN</c> environment variable,</item>
///   <item>ClaudeCounter's own session, refreshed here as needed,</item>
///   <item>Claude Code's credentials file, read-only, as a bootstrap.</item>
/// </list>
/// Only tier 2 is ever refreshed. Tier 3 exists so a user with the CLI already
/// installed sees their usage immediately on first launch, without ClaudeCounter
/// touching a refresh-token chain it does not own.
/// </summary>
public sealed class TokenProvider
{
    public const string EnvVarName = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>Refresh slightly early so a token never dies mid-request.</summary>
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Serializes a refresh across every ClaudeCounter process for this Windows
    /// user (fast user switching, concurrent RDP sessions). The server rotates
    /// the refresh token on every use and invalidates the old one immediately,
    /// so two processes refreshing the same session at once leaves one of them
    /// holding a token the server has already burned.
    /// </summary>
    private const string RefreshMutexName = @"Global\ClaudeCounter_SessionRefresh";
    private static readonly TimeSpan RefreshLockTimeout = TimeSpan.FromSeconds(10);

    private readonly ISessionStore _session;
    private readonly ITokenRefresher _refresher;
    private readonly CliCredentialsFile _cliFile;
    private readonly Func<DateTimeOffset> _now;

    // No default session store: EncryptedSessionStore needs a platform-specific
    // IDataProtector (DPAPI, Secret Service, ...) that this project does not
    // know about, so the caller must always supply one.
    public TokenProvider(
        ISessionStore session,
        ITokenRefresher? refresher = null,
        CliCredentialsFile? cliFile = null,
        Func<DateTimeOffset>? now = null)
    {
        _session = session;
        _refresher = refresher ?? new OAuthTokenRefresher();
        _cliFile = cliFile ?? new CliCredentialsFile();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True when ClaudeCounter has a session of its own.</summary>
    public bool HasOwnSession => _session.Read() is not null;

    public async Task<CredentialResult> GetTokenAsync(CancellationToken ct)
    {
        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
            return new CredentialResult.Token(env.Trim(), TokenSource.EnvironmentVariable);

        if (_session.Read() is { } session)
        {
            if (session.ExpiresAt - ExpiryGrace > _now())
                return new CredentialResult.Token(session.AccessToken, TokenSource.OwnSession);

            return await RefreshUnderLockAsync(ct);
        }

        return FromCliBootstrap();
    }

    private async Task<CredentialResult> RefreshUnderLockAsync(CancellationToken ct)
    {
        Mutex? refreshLock = null;
        var acquired = false;
        try
        {
            try
            {
                refreshLock = new Mutex(false, RefreshMutexName);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Could not create or open the named mutex - a locked-down
                // environment, or an ACL conflict with another owner. Refreshing
                // without cross-process coordination is still correct in the
                // ordinary single-session case; it only reintroduces the race
                // this lock exists to close under concurrent sessions.
                Log.Warn($"Could not acquire the session refresh lock: {e.Message}");
            }

            if (refreshLock is not null)
            {
                try
                {
                    acquired = refreshLock.WaitOne(RefreshLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder's process died while it held the
                    // lock. The mutex is still ours to use.
                    acquired = true;
                }
                if (!acquired)
                    Log.Warn("Timed out waiting for the session refresh lock; refreshing anyway.");
            }

            // Another process may have refreshed the session while we were
            // waiting for the lock - re-read rather than reuse the caller's
            // now-possibly-stale copy.
            if (_session.Read() is not { } session)
                return FromCliBootstrap();
            if (session.ExpiresAt - ExpiryGrace > _now())
                return new CredentialResult.Token(session.AccessToken, TokenSource.OwnSession);

            switch (await _refresher.RefreshAsync(session.RefreshToken, ct))
            {
                case TokenResult.Success success:
                    return Persist(success.Tokens);

                case TokenResult.Rejected rejected:
                    // Permanently dead. Drop it so the next poll falls through
                    // to bootstrap instead of re-posting a token the server has
                    // already refused.
                    Log.Warn($"Stored session was rejected; signing out. {rejected.Message}");
                    _session.Clear();
                    break;

                case TokenResult.Transient transient:
                    // Do not fall through: a stale bootstrap token would mask a
                    // network problem and make it harder to diagnose.
                    return new CredentialResult.TransientError(transient.Message);
            }
        }
        finally
        {
            if (acquired && refreshLock is not null)
            {
                try { refreshLock.ReleaseMutex(); }
                catch (ApplicationException) { /* not owned - should not happen given `acquired` */ }
            }
            refreshLock?.Dispose();
        }

        return FromCliBootstrap();
    }

    private CredentialResult FromCliBootstrap()
    {
        switch (_cliFile.Read())
        {
            case CredentialsRead.Ok ok:
                // No expiry recorded, or still valid: usable as-is.
                if (ok.Value.ExpiresAt is not { } expiresAt || expiresAt - ExpiryGrace > _now())
                    return new CredentialResult.Token(ok.Value.AccessToken, TokenSource.CliBootstrap);

                // Expired. We will NOT refresh it - see CliCredentialsFile.
                return new CredentialResult.SignInRequired(
                    "Claude Code's session has expired");

            case CredentialsRead.Missing:
                return new CredentialResult.SignInRequired("Not signed in");

            case CredentialsRead.Malformed malformed:
                return new CredentialResult.SignInRequired(
                    $"Claude Code's credentials file is unreadable: {malformed.Message}");

            default:
                return new CredentialResult.SignInRequired("Not signed in");
        }
    }

    private CredentialResult Persist(OAuthTokens tokens)
    {
        try
        {
            _session.Write(OAuthSession.FromTokens(tokens, _now()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The token is good in memory even if it could not be saved; use it
            // now and try again next cycle.
            Log.Warn($"Refreshed session could not be saved: {e.Message}");
        }
        OAuthConfig.LogIfBroaderThanNeeded(tokens.Scopes);
        return new CredentialResult.Token(tokens.AccessToken, TokenSource.OwnSession);
    }
}

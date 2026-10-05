namespace ClaudeCounter.Core.Auth;

/// <summary>
/// Every endpoint, identifier and scope the OAuth flow uses. Kept in one place
/// because these are undocumented values borrowed from the Claude Code CLI: if
/// Anthropic changes one, this is the only file that needs editing.
/// </summary>
public static class OAuthConfig
{
    /// <summary>
    /// Claude Code's public OAuth client id. Not a secret - it is embedded in
    /// the CLI - but it does mean ClaudeCounter identifies itself as that
    /// client, which is documented in the README and SECURITY.md.
    /// </summary>
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>
    /// Where Anthropic sends the browser after approval. This is a real page
    /// that displays the code for the user to copy, not a loopback listener,
    /// so the flow needs no local HTTP server and no firewall exception.
    /// </summary>
    public const string RedirectUri = "https://console.anthropic.com/oauth/code/callback";

    /// <summary>
    /// The scope set ClaudeCounter requests and the authorization server has
    /// been verified (2026-07-31, live sign-in) to grant for this client id.
    /// <c>user:profile</c> is what the usage endpoint requires;
    /// <c>user:inference</c> comes along with it. Deliberately excludes
    /// <c>org:create_api_key</c>, which the Claude Code CLI's full scope
    /// string carries and would let the stored token mint API keys - more
    /// authority than reading usage needs. See <see cref="FullClientScopes"/>
    /// if the server ever stops accepting this narrower set.
    /// </summary>
    public const string Scopes = "user:profile user:inference";

    /// <summary>
    /// The client's full configured scope set, as the Claude Code CLI itself
    /// requests it. Not used - kept only as a documented fallback in case a
    /// future change to the authorization server rejects <see cref="Scopes"/>.
    /// </summary>
    public const string FullClientScopes = "org:create_api_key user:profile user:inference";

    /// <summary>
    /// claude.ai is correct for Pro/Max subscriptions, which is what this app
    /// reports on. Console-only accounts authorize at
    /// https://console.anthropic.com/oauth/authorize instead.
    /// </summary>
    public static readonly Uri Authorize = new("https://claude.ai/oauth/authorize");

    public static readonly Uri Token = new("https://console.anthropic.com/v1/oauth/token");

    /// <summary>
    /// Scope names are not secrets, so logging them is safe. Worth a note even
    /// on the happy path: a grant that includes org:create_api_key means the
    /// stored token can do more than read usage, and that is easy to forget
    /// once sign-in is working.
    /// </summary>
    public static void LogIfBroaderThanNeeded(string? grantedScopes)
    {
        if (grantedScopes?.Contains("org:create_api_key") == true)
            Log.Warn($"Granted OAuth scope includes org:create_api_key: '{grantedScopes}'");
    }
}

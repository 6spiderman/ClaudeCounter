using ClaudeCounter.Core;
using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.UI;

/// <summary>
/// What the tray menu's sign-in entry says, shared by the Windows and Linux
/// trays. Pure.
/// </summary>
public static class SignInMenu
{
    /// <param name="source">Where the last poll's token came from (null before the first poll, or when there was none).</param>
    /// <param name="signInNeeded">The last poll needs the user to sign in (no token, or it expired).</param>
    /// <param name="accountEmail">The signed-in account, when known.</param>
    /// <returns>
    /// The entry's text; whether it is clickable (it opens sign-in); and
    /// whether to show a separate "Sign in with another account..." entry.
    /// </returns>
    public static (string Header, bool Enabled, bool ShowSwitchAccount) For(
        TokenSource? source, bool signInNeeded, string? accountEmail)
    {
        if (signInNeeded)
            return ("Sign in to Claude...", true, false);

        return source switch
        {
            TokenSource.OwnSession => (
                accountEmail is { Length: > 0 } email ? $"Signed in as {email}" : "Signed in to Claude",
                false,
                true),
            TokenSource.CliBootstrap => ("Sign in to Claude... (using Claude Code's session)", true, false),
            TokenSource.EnvironmentVariable => ($"Using the {TokenProvider.EnvVarName} token", false, false),
            _ => ("Sign in to Claude...", true, false),
        };
    }
}

# Security

## Reporting a vulnerability

Please report privately through
[GitHub Security Advisories](https://github.com/6spiderman/ClaudeCounter/security/advisories/new)
rather than opening a public issue. I will acknowledge within a few days.

Only the latest release is supported. Fixes ship in a new release, not as
patches to older versions.

## What the app stores, and where

| Path | Contents | Protection |
| --- | --- | --- |
| `%LocalAppData%\ClaudeCounter\session.dat` | ClaudeCounter's own OAuth access token, refresh token and expiry | Encrypted with [DPAPI](https://learn.microsoft.com/en-us/windows/win32/seccrypto/cryptography-functions) at `CurrentUser` scope - only your Windows account on this machine can decrypt it |
| `%AppData%\ClaudeCounter\settings.json` | Poll interval, thresholds, autostart, update-check preferences | Plain JSON. Contains no secrets. |
| `%LocalAppData%\ClaudeCounter\logs\claudecounter.log` | Append-only diagnostic log | Plain text. Contains no tokens by design; see below. |

DPAPI keys are bound to your Windows user account **and** the machine. Copying
`session.dat` to another PC leaves it undecryptable, and the app treats that as
"not signed in" rather than as an error.

## Known limitations

Two things below are not bugs to be fixed; they are properties of every
no-admin Windows desktop app, stated here deliberately rather than left
implicit.

**Anything running as you can read your session.** DPAPI protects
`session.dat` against a different Windows account, or the same account on a
different machine. It does not protect against another process running under
*your own* account: that process can call the same decryption API ClaudeCounter
does. No desktop application can close this boundary - it is the boundary of
"logged in as you." If your PC is compromised at the user-account level,
assume anything ClaudeCounter has stored is readable too.

**The install folder and autostart entry are user-writable.** ClaudeCounter
installs to `%LocalAppData%\Programs\ClaudeCounter` and registers itself in
`HKCU\...\Run` so it starts with Windows - both without requesting admin
rights, which is the standard trade-off for a no-UAC installer (the same
pattern Visual Studio Code and Microsoft Teams use). The consequence is that
anything else running as you could, in principle, replace the installed
executable or edit that registry value. Code signing would make that kind of
tampering visible - an unsigned replacement would break the publisher
signature - and is the intended future mitigation; it does not change the
install model itself, which is already the right trade-off for this class of
app.

## Where data goes

ClaudeCounter talks to exactly two hosts, both Anthropic's:

- `https://console.anthropic.com/v1/oauth/token` - OAuth authorization-code
  exchange and refresh.
- `https://api.anthropic.com/api/oauth/usage` - reads your plan usage.

Plus, only when you explicitly ask it to check for updates:

- `https://api.github.com/repos/6spiderman/ClaudeCounter/releases/latest` -
  sends no data beyond the request itself. Can be turned off in Settings.

There is no telemetry, no analytics and no crash reporting. Your tokens and
your usage figures are never sent anywhere else.

## Tokens are never logged

The sign-in flow handles a PKCE verifier, an authorization code, an access
token and a refresh token. None of these are written to the log, put in an
exception message, or shown in the UI. A unit test runs a complete sign-in
against a temporary log file and asserts that none of the secret values appear
in it.

If you find a code path that leaks one, that is a security bug - please report
it.

## OAuth scope

ClaudeCounter signs in with the same public OAuth client id that the Claude
Code CLI embeds, but requests only `user:profile` and `user:inference` -
read-level scopes, verified against the live authorization server. It does
**not** request `org:create_api_key`, so the stored token cannot create API
keys or spend against your organisation's billing; it can only read your plan
usage.

The Claude Code CLI's own session carries the broader scope set, since it
needs `org:create_api_key` for other functionality. If you are relying on the
read-only bootstrap from Claude Code's credentials file (see
[Authentication](README.md#authentication)) rather than signing in to
ClaudeCounter directly, the token in play is the CLI's broader one, not
ClaudeCounter's own.

## Unofficial software

ClaudeCounter is not affiliated with, endorsed by, or supported by Anthropic.
It uses an undocumented usage endpoint and a public client id belonging to
another application. Both can change or be revoked without notice. Use it at
your own risk and in accordance with Anthropic's terms of service.

## Unsigned binaries

Releases are not code-signed. Windows SmartScreen will warn about them.
Every release publishes a `SHA256SUMS.txt`; verify your download before
running it:

```powershell
Get-FileHash .\ClaudeCounter-1.0.0-x64-setup.exe -Algorithm SHA256
```

If you would rather not trust a binary at all, the project is small enough to
read end to end and build yourself.

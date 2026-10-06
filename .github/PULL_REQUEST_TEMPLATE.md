## Summary

<!-- What does this change and why? -->

Closes #

## Tests

<!-- Which tests did you add or change? `dotnet test` output is enough. -->

- [ ] `dotnet test` passes
- [ ] New behaviour has unit tests

## Manual verification

<!-- Tick what you actually ran, delete what does not apply. -->

- [ ] Tray icon renders and updates
- [ ] Flyout opens, closes, and shows correct values
- [ ] Sign-in flow completes end to end
- [ ] Sign out (if sign-in or session code changed): confirm, the icon clears,
      and the session is gone from disk/keyring
- [ ] `~/.claude/.credentials.json` is byte-identical before and after a run
      (`Get-FileHash $HOME\.claude\.credentials.json`, or `sha256sum` on Linux)
- [ ] No token, refresh token, authorization code or PKCE verifier appears in
      the log (`%LocalAppData%\ClaudeCounter\logs\claudecounter.log`, or
      `~/.local/share/ClaudeCounter/logs/claudecounter.log` on Linux)
- [ ] Upgrade over an existing install works (if packaging changed)
- [ ] Linux: logging out with the app running (flyout opened at least once)
      is not blocked or delayed (if the Linux app changed)

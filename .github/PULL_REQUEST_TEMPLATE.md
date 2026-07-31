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
- [ ] `~/.claude/.credentials.json` is byte-identical before and after a run
      (`Get-FileHash $HOME\.claude\.credentials.json`)
- [ ] No token, refresh token, authorization code or PKCE verifier appears in
      `%LocalAppData%\ClaudeCounter\logs\claudecounter.log`
- [ ] Upgrade over an existing install works (if packaging changed)

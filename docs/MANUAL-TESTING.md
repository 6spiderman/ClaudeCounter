# Manual test guide

Run through this before tagging a release. `dotnet test` covers the logic;
this covers everything that only shows up on a real machine.

Times are rough. The whole pass is about 45 minutes, plus one overnight wait
for the section that matters most.

---

## 0. Build what you are going to test

```powershell
dotnet test
dotnet publish src/ClaudeCounter/ClaudeCounter.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:Version=1.0.0 -o dist/win-x64
```

- [ ] 193+ tests pass
- [ ] `dist/win-x64` contains **exactly one file**, `ClaudeCounter.exe`
      (a stray `.dll` means the portable zip would be broken)

Then check the app actually starts. Unit tests do not exercise the WinForms
wiring, and a first-run crash shipped once because every dialog was verified in
isolation while the real executable never was:

```powershell
pwsh -File packaging/smoke-test.ps1
```

It stashes your real settings and session, runs a genuine first-run, and puts
everything back.

- [ ] Smoke test passes

Keep a log window handy throughout:

```powershell
Get-Content "$env:LOCALAPPDATA\ClaudeCounter\logs\claudecounter.log" -Wait -Tail 20
```

---

## 1. ClaudeCounter and Claude Code coexist

ClaudeCounter holds its own OAuth grant, separate from the Claude Code CLI's.
The two are meant to be independent, so signing in to one must never disturb
the other. Verify that on a machine where both are in use.

1. Confirm the CLI works: `claude -p "say hi"`
2. Back up the credentials file so any surprise is recoverable:
   ```powershell
   Copy-Item "$HOME\.claude\.credentials.json" "$HOME\.claude\.credentials.backup.json"
   ```
3. Sign in through ClaudeCounter (section 3 below).
4. `claude -p "say hi"` -> still works?
5. Use Claude Code normally for long enough that **its** token expires and it
   refreshes (an hour or so). Then `claude -p "say hi"` again.
6. Let ClaudeCounter refresh its own session: leave it running past its token
   expiry, or restart it after several hours. Look for a `Token source` line in
   the log.
7. `claude -p "say hi"` one final time.

- [ ] Steps 4, 5 and 7 all succeed

If any of them forces a `/login`, the two grants are interfering. Restore the
backup and open an issue with the log; the fallback is to use the read-only
bootstrap path only and accept that usage goes stale while the CLI is idle.

### Scopes

Verified 2026-07-31: the authorization server accepts a narrower scope set
than the Claude Code CLI's full one. ClaudeCounter requests only
`OAuthConfig.Scopes` ("user:profile user:inference"); the stored token cannot
mint API keys. The full client scope set is kept as `OAuthConfig.FullClientScopes`,
documented but unused, in case a future server change rejects the narrow one.

If a sign-in ever fails specifically with `invalid_scope`, that is the signal
the server stopped accepting the narrow set - point `AuthorizeRequest.Create`
back at `OAuthConfig.FullClientScopes` and update SECURITY.md accordingly.

- [ ] Sign-in succeeds and the log shows no "Granted OAuth scope includes
      org:create_api_key" warning

---

## 2. First run and onboarding

Start from clean so you see what a new user sees:

```powershell
Remove-Item "$env:APPDATA\ClaudeCounter" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\ClaudeCounter\session.dat" -Force -ErrorAction SilentlyContinue
```

- [ ] The wizard appears **in front**, not behind your other windows
- [ ] It appears in the taskbar
- [ ] Step 1 shows a green sample tray icon reading `42`
- [ ] Back is disabled on step 1; Next moves through all three steps
- [ ] "Skip for now" is offered on steps 1 and 2, hidden on step 3
- [ ] Changing the interval and the autostart checkbox on step 3, then Finish,
      is reflected in Settings afterwards
- [ ] Closing the wizard with the X does **not** bring it back on next launch

---

## 3. Signing in

Tray menu -> **Sign in to Claude...**

- [ ] Dialog opens in front and in the taskbar
- [ ] The code box, Paste and Connect are **disabled** until you open the browser
- [ ] "Open Claude in your browser" launches your default browser to claude.ai
- [ ] "copy link instead" puts a working URL on the clipboard
- [ ] After approving, pasting the code and clicking Connect shows
      "Signed in", then the dialog closes itself after about a second
- [ ] The tray icon updates within a second or two - no waiting for the next poll

### Error paths

Each of these should give a *different*, specific message, and never a stack
trace or a crash:

- [ ] Paste `not-a-code` -> "That doesn't look like a code..."
- [ ] Paste `abc#wrong-state` -> "...from a different sign-in attempt..."
- [ ] Sign in, then click "Start over" and paste the **old** code ->
      state mismatch, not a confusing server error
- [ ] Wait 10 minutes before pasting a valid code -> "Anthropic rejected the
      code. Codes expire..."
- [ ] Turn off Wi-Fi, then Connect -> "Couldn't reach Anthropic...", and the
      dialog stays usable so you can retry
- [ ] Paste the **whole callback URL** from the address bar instead of the code
      -> works

---

## 4. The thing this release exists to fix

ClaudeCounter must never touch Claude Code's credentials.

```powershell
$cred = "$HOME\.claude\.credentials.json"
$before = (Get-FileHash $cred -Algorithm SHA256).Hash
$before
```

Now leave ClaudeCounter running for **longer than its access token lifetime** -
overnight is easiest. It must refresh its own session at least once; confirm by
finding a refresh in the log.

```powershell
(Get-FileHash $cred -Algorithm SHA256).Hash -eq $before
```

- [ ] Returns `True`
- [ ] `claude -p "say hi"` still works
- [ ] `/status` inside Claude Code shows a healthy session

**If the hash changed, something wrote to that file and the fix is not
working.** That is a release blocker.

---

## 5. Falling back to Claude Code's session

For a user who has the CLI but has not signed in to ClaudeCounter yet.

```powershell
Remove-Item "$env:LOCALAPPDATA\ClaudeCounter\session.dat" -Force
```

Restart the app with Claude Code signed in.

- [ ] Usage appears immediately - no sign-in needed to see numbers
- [ ] The flyout shows the amber line *"Using Claude Code's session - sign in
      for your own."*
- [ ] A **Sign in** button sits next to Refresh in the flyout, and opens the dialog
- [ ] The tray menu entry reads "Sign in to Claude... (using Claude Code's session)"
- [ ] The log says `Token source: Claude Code's session (bootstrap)`
- [ ] After signing in, the amber line and the extra button disappear

---

## 6. Damaged and missing state

None of these may crash the app.

- [ ] Corrupt the session: `Set-Content "$env:LOCALAPPDATA\ClaudeCounter\session.dat" "garbage"`
      -> restart -> prompts to sign in, does not throw
- [ ] Delete `session.json`'s parent folder entirely -> restart -> still starts
- [ ] Corrupt settings: put `not json` in `%AppData%\ClaudeCounter\settings.json`
      -> restart -> falls back to defaults
- [ ] Rename `~/.claude/.credentials.json` away with no session present ->
      flyout says "Not signed in - sign in to Claude"
- [ ] Set `CLAUDE_CODE_OAUTH_TOKEN` to a valid token -> it wins over everything,
      and the log says so

---

## 7. No secrets in the log

Users will paste log excerpts into bug reports.

```powershell
$log = "$env:LOCALAPPDATA\ClaudeCounter\logs\claudecounter.log"
Select-String -Path $log -Pattern 'sk-ant-','accessToken','refreshToken','code_verifier','Bearer '
```

- [ ] No matches, including after a failed sign-in and a rejected refresh

---

## 8. Everyday behaviour

- [ ] Tray icon shows the five-hour percentage and is readable at your DPI
- [ ] It goes amber above the warn threshold and red above critical
- [ ] Hover shows a tooltip with session and weekly figures
- [ ] Left-click opens the flyout; clicking the icon again closes it (rather
      than instantly reopening)
- [ ] Clicking elsewhere dismisses the flyout
- [ ] Countdowns tick while the flyout stays open
- [ ] **Refresh now** updates immediately
- [ ] Changing thresholds in Settings recolours the icon straight away
- [ ] Sleep the PC for a few minutes; on resume the icon refreshes promptly
      instead of showing stale numbers
- [ ] Launching a second copy does nothing (single instance)
- [ ] Switch Windows between light and dark mode -> the flyout follows

---

## 9. Updates and About

- [ ] **About ClaudeCounter...** shows the icon and the right version
- [ ] Project page / Releases / License links open
- [ ] **Open log folder** opens Explorer with the log selected
- [ ] "Check for updates" reports up to date, or offers a download
- [ ] Turning off "Check for updates automatically" stops the daily check
- [ ] A dev build (`0.0.0-dev`) never nags about updates

To test the "update available" path without publishing anything, temporarily
build with an older version: `-p:Version=0.0.1`.

- [ ] A tray menu entry and a flyout line appear - and **no popup**

---

## 10. Installer

Best on a clean VM. Minimum: a spare user account.

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
  /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=(Resolve-Path dist\win-x64) `
  packaging\inno\ClaudeCounter.iss
```

- [ ] Installer runs with **no UAC prompt**
- [ ] Installs to `%LocalAppData%\Programs\ClaudeCounter`
- [ ] Start Menu shortcut works
- [ ] "Launch ClaudeCounter" on the final page works
- [ ] The app appears under Settings -> Apps with the right icon, name, version
      and publisher
- [ ] Enable start-with-Windows, sign out and back in -> it starts, **once**
      (a second autostart mechanism would launch it twice)

Upgrade:

- [ ] With ClaudeCounter **running**, install again -> prompted to close it,
      rather than a locked-file error
- [ ] After upgrading, your settings and session survive - no re-sign-in

Uninstall:

- [ ] Removes the Start Menu shortcut and the install folder
- [ ] Removes the `HKCU\...\Run` value:
      `Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name ClaudeCounter`
      should error
- [ ] Asks before deleting settings, and honours both answers

Silent install, as winget will invoke it:

```powershell
.\dist\ClaudeCounter-1.0.0-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
```

- [ ] Completes with no UI and no prompts

---

## 11. Portable zip

- [ ] Unzips and runs from an arbitrary folder
- [ ] Copy the zip to a **different PC**: it starts and prompts to sign in.
      DPAPI keys are machine-bound, so the old `session.dat` cannot decrypt -
      it must read as "signed out", never crash

---

## 12. SmartScreen

On a machine that has never seen this build:

- [ ] "Windows protected your PC" appears
- [ ] **More info -> Run anyway** works
- [ ] The hash matches `SHA256SUMS.txt`:
      `Get-FileHash .\ClaudeCounter-1.0.0-x64-setup.exe -Algorithm SHA256`

---

## 13. arm64

Only if you have the hardware. Do not submit the arm64 installer to winget
until someone has ticked these.

- [ ] Installer runs and the app starts
- [ ] Tray icon renders correctly
- [ ] Sign-in completes

---

## Release blockers

Everything above is worth fixing, but these four stop a release outright:

1. Section 4 - the credentials file hash changed.
2. Section 1 - signing in kills the Claude Code session.
3. Section 7 - a secret reached the log.
4. Section 0 - the publish output is more than one file.

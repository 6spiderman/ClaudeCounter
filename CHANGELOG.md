# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-10-06

### Added

- **Linux.** A native Linux tray app (`ClaudeCounter.Linux`, on Avalonia) with
  the same tray icon, flyout, tooltip, sign-in, first-run wizard and settings
  as the Windows build. Shipped as `.deb` and `.rpm` packages and a portable
  `.tar.gz`, for x64 and arm64 (arm64 is cross-built and not yet tested on
  real hardware). Tested on Kubuntu 26.04 with KDE Plasma 6. The session is
  kept in the desktop keyring (KWallet, GNOME Keyring) when one is available.
- `ClaudeCounter.Core`: everything platform-neutral (polling, OAuth, usage
  models, settings) moved into a shared library that both builds use
  unchanged.

### Fixed (Linux)

- **ClaudeCounter no longer blocks logout or shutdown.** Once the flyout had
  been opened in a session, the app answered KDE Plasma's logout request
  (X11 session management) by cancelling it, so logging out or shutting down
  did nothing until ClaudeCounter was quit by hand. The flyout now only
  refuses a normal close and lets a logout or app shutdown through.
- Exiting on `SIGTERM` (what systemd sends at shutdown) is bounded: if the
  clean exit has not finished within five seconds it is forced, instead of
  holding shutdown for systemd's full 90-second stop timeout.
- No more orphaned `dbus-monitor` process. Sleep/resume detection now
  subscribes to systemd-logind in-process instead of running a helper that
  could outlive the app.
- The `.deb` no longer pulls GNOME Shell onto KDE and other desktops. It
  recommended the GNOME AppIndicator extension, which apt installs by default
  and which depends on GNOME Shell; it is now only suggested.
- The `.deb`/`.rpm` declare the system libraries the app needs (OpenSSL,
  fontconfig, the X11 client libraries), and recommend `libsecret-tools` so
  the session goes into KWallet/GNOME Keyring rather than the file fallback.
- The flyout opens next to the panel (bottom-right on Plasma's default
  layout) instead of always top-right, and clicking the tray icon while it is
  open now closes it instead of reopening it.
- Startup no longer freezes the tray for up to three seconds while checking
  for a system tray host.
- Uninstalling no longer leaves a login autostart entry pointing at a missing
  program.

## [1.0.0] - 2026-07-31

### Fixed

- **The app no longer crashes on first run.** Onboarding was deferred with
  `Control.BeginInvoke` on the flyout, which has no window handle until it is
  first shown, so a fresh install died with `InvalidOperationException` before
  the wizard appeared. It now defers with a one-shot timer, which needs no
  handle.
- The first-run wizard no longer looks stuck after signing in. It showed
  "Fetching your usage..." and then sat there - wording that only made sense in
  the standalone dialog, which closes itself a moment later. The wizard now
  confirms the tray icon is live and focuses **Next**, and the tray refreshes
  immediately on sign-in rather than waiting for the wizard to close.
- Unhandled exceptions are written to the log and shown in a message box naming
  the log path and the issue tracker, instead of dying in Windows Error
  Reporting with nothing recorded. A crash the user cannot report is the worst
  kind.
- **ClaudeCounter no longer logs you out of Claude Code.** Anthropic rotates the
  OAuth refresh token on every refresh and invalidates the old one immediately,
  so two programs sharing `~/.claude/.credentials.json` eventually fight and one
  gets a permanent `invalid_grant` that only `/login` clears. Previous versions
  refreshed Claude Code's tokens in place and hit exactly this. ClaudeCounter now
  holds its own OAuth grant and **never writes to that file**.
- The single-file build really is a single file. The Windows Desktop runtime pack
  was leaving five WPF native DLLs beside the executable, so copying just
  `ClaudeCounter.exe` out of a release produced an app that would not start.
- A dead session no longer re-posts a refresh token the server has already
  refused, every two minutes, indefinitely.

### Added

- **Sign in to Claude...** in the tray menu: a full OAuth 2.0 authorization-code
  flow with PKCE. Opens claude.ai in your browser, you paste the code back.
  ClaudeCounter no longer needs the Claude Code CLI to be installed at all.
- A first-run wizard covering what the app does, sign-in, and preferences.
- Claude Code's credentials file is still read as a **read-only** bootstrap, so
  existing CLI users see their usage immediately on first launch. The flyout
  says so, and offers a Sign in button.
- ClaudeCounter's own session is stored DPAPI-encrypted at
  `%LocalAppData%\ClaudeCounter\session.dat`, readable only by your Windows
  account on that machine.
- An application icon, shown on the executable, in dialogs and in the taskbar.
- **About ClaudeCounter** in the tray menu: version, project links, a manual
  update check, and a shortcut to the log folder.
- Automatic update checks against GitHub Releases, at most once a day and only
  after the first successful poll. Surfaced as a tray menu entry and a line in
  the flyout, never as a popup. Can be turned off in Settings.
- **Open log folder** in the tray menu.
- A per-user installer (no admin rights) built with Inno Setup, for x64 and
  arm64, alongside portable zips. Uninstalling removes the start-with-Windows
  registry entry and offers to remove your settings.
- A tag-triggered release workflow that tests, publishes both architectures,
  builds the installers, generates `SHA256SUMS.txt` and creates the GitHub
  release.
- winget manifest templates and submission notes under `packaging/winget/`.
- Project is now open source under the MIT license, with contribution, security
  and issue-reporting docs.
- Continuous integration on every push and pull request, including a
  single-file publish check and a startup smoke test that launches the real
  executable (`packaging/smoke-test.ps1`).

### Changed

- The version now comes from the release tag rather than being unset.
- Dialogs appear in the taskbar and come to the front when opened. Previously a
  dialog could open behind the window you were working in and look like nothing
  had happened, because a tray app has no owner window.
- Error messages from the token endpoint report the OAuth error code only. The
  raw response body is never logged or shown, because an error body can echo
  back the credential that was rejected.

[1.1.0]: https://github.com/6spiderman/ClaudeCounter/releases/tag/v1.1.0
[1.0.0]: https://github.com/6spiderman/ClaudeCounter/releases/tag/v1.0.0

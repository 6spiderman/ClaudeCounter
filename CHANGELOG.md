# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.2.0] - 2026-08-11

### Added

- **Alert popups.** A configurable popup when a usage window crosses your
  critical threshold or hits 100%, independently for the 5-hour session, the
  rolling 7-day window, and the per-model (Opus/Sonnet) windows. Placement is
  near the tray or centered on screen, all configurable (or turned off
  entirely) in Settings.
- **Configurable alert repeat cadence.** A new "Repeat every" option for alert
  popups (Settings -> Alerts) lets you choose how often ClaudeCounter re-shows
  a popup while a window stays at or above an already-alerted level, instead
  of only once per crossing. Off by default.
- **ClaudeBackup**, a standalone worker that backs up a subset of your Claude
  config (settings, CLAUDE.md, commands, agents, top-level plugin manifests)
  to a private GitHub repo and/or a Drive destination on a schedule you set,
  or on demand from the tray menu or Settings. Never sends anything to more
  than one destination without your say-so, never backs up credential-shaped
  files (a denylist is applied before anything is read), and never writes a
  secret to `backup.json` or any log. The GitHub repo you point it at **must
  be private** - ClaudeCounter has no way to verify that automatically, so it
  warns in the log and says so in Settings. Included by default when you
  install or upgrade; choose "Custom" instead of "Full installation" on the
  installer's Components page if you would rather leave it out - the tray
  hides every backup control when the worker is not installed.
- **Named backup destinations.** The Backup tab's "Back up to" list now names
  every destination directly - GitHub, Google Drive, OneDrive, Dropbox, NAS /
  network share, and rclone remote (advanced) - instead of a generic "Drive"
  destination with a second, nested transport dropdown you had to open before
  either word ever appeared anywhere in Settings. Picking Google Drive,
  OneDrive, Dropbox, or NAS / network share auto-detects and fills in the
  folder path for you right away (de-duplicating OneDrive's several
  environment variables, and offering a mapped NAS drive as its UNC path
  rather than the drive letter, since a drive letter mapping is not
  guaranteed to resolve under a scheduled, non-interactive backup run); a
  **Detect...** button lets you search again by hand, e.g. after signing into
  a sync client that was not set up yet. Under the hood this is still the
  same single Drive destination as before, so GitHub plus exactly one
  cloud/NAS destination can be active at a time, and picking a different
  cloud/NAS option replaces whichever one was configured before - the Backup
  tab says so plainly next to the selector rather than leaving it to be
  discovered by surprise. See
  [docs/GOOGLE-DRIVE-SETUP.md](docs/GOOGLE-DRIVE-SETUP.md), sync-folder
  method first.
- "Back up now" (tray menu and Settings) now waits for the backup to finish
  and reports the result - previously it fired the worker and forgot about
  it, so a failing backup looked identical to a successful one.
- **Advanced backup settings**, behind a new "Advanced..." button on the
  Backup tab: a missed scheduled backup (e.g. the machine was asleep) now
  runs as soon as possible afterwards by default, the task can require a
  network connection before starting, and a failed run retries automatically
  (every 15 minutes, up to 3 times, all by default). The task no longer
  requires being plugged in to run or to keep running - unlike Windows' own
  new-task defaults, which would have silently reintroduced the
  missed-backup problem this exists to fix.
- **Drive backup retention**, in the same Advanced dialog: keep only the most
  recent N backups, delete anything older than N days, or both. Off by
  default. Applied only after a successful upload, never deletes the single
  most recent backup no matter how the settings are set, and only ever
  considers this app's own `claude-backup-*.zip` files - never anything else
  stored in the same remote folder. Applies to both Drive transports.

### Changed

- When alert repeat is turned on, a new 5-hour session now resets the repeat
  timer for every window - 5-hour, weekly, and per-model - not just its own,
  so a fresh 5-hour period does not leave another window's popup waiting out
  a stale interval.

### Fixed

- **Usage alert popups no longer repeat every few minutes for the same
  threshold crossing.** A quirk in how the API reports each window's reset
  time made ClaudeCounter think the reset time had changed on every poll,
  which cleared the "already alerted" flag and re-showed the same popup
  again and again instead of once.
- **The Critical (90%) alert popup now has a red accent**, matching the 100%
  popup. Previously it had no accent color at all and ended up looking less
  urgent than the amber 75% Warn popup.
- A repointed GitHub remote or a changed backup branch now actually takes
  effect on the next scheduled or manual run, instead of silently continuing
  to push to whichever remote/branch was configured the very first time.
- Two backup runs (a scheduled run racing a manual "back up now", or two
  triggers close together) can no longer interleave against the same staging
  repo and delete each other's files from the remote backup - ClaudeBackup.exe
  now takes a single-instance lock, the same way the tray app already does.

## [1.1.1] - 2026-10-06

### Fixed (Linux)

- The Settings dialog no longer cuts off its **OK** and **Cancel** buttons. It
  had a fixed height that was too short for the content at KDE Plasma's
  default font size. The Settings, Sign in and first-run dialogs now size
  themselves to their content, so a longer error message or the fallback
  sign-in link can no longer be clipped either.
- The first-run wizard now says where your sign-in is kept: your desktop's
  keyring, or an encrypted file if there is none.

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

[1.2.0]: https://github.com/6spiderman/ClaudeCounter/releases/tag/v1.2.0
[1.1.1]: https://github.com/6spiderman/ClaudeCounter/releases/tag/v1.1.1
[1.1.0]: https://github.com/6spiderman/ClaudeCounter/releases/tag/v1.1.0
[1.0.0]: https://github.com/6spiderman/ClaudeCounter/releases/tag/v1.0.0

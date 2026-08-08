# ClaudeBackup - Design

## Context

Users accumulate valuable, hand-crafted Claude configuration under `~/.claude`
(`settings.json`, `CLAUDE.md`, custom `commands/`, `agents/`, plugin config) and
`~/.claude.json`. There is currently no way to back this up. If the machine dies
or the folder is corrupted, that work is gone.

This feature adds a **scheduled backup tool** that copies a user-selected subset of
the Claude config to **GitHub (a private repo)** and/or **Google Drive (via
rclone)**, on a **Windows Task Scheduler** schedule, so it runs even when the tray
app is closed. The tray app is the control panel; a separate worker executable does
the work.

**Security is the defining constraint**: `~/.claude/.credentials.json` holds live
OAuth tokens and must never be uploaded anywhere. Secret exclusion is hard-coded
and cannot be overridden by config.

## Goals / Non-goals

**Goals**
- Standalone `ClaudeBackup.exe` (same solution/repo/installer, separate process).
- Back up a **configurable** include/exclude set of Claude config files.
- Destinations: **GitHub** (git push to a pre-created private repo) and
  **Google Drive** (zip + `rclone copy`). Either or both.
- Schedule via Windows Task Scheduler, registered from the tray UI.
- Optional install component so users who do not want it never get the exe.
- Never upload secrets, under any configuration.

**Non-goals**
- No restore tooling in this version (backups are plain git history / zips the
  user can restore by hand). Restore is a future spec.
- No bundling of rclone or git - both are detected, not shipped.
- No cloud backends beyond GitHub and Drive-via-rclone.
- No backup of `projects/` session history by default (large; opt-in only).

## Architecture

**Two executables, one product** (see the standalone-vs-integrated decision in the
brainstorming thread):

- **`ClaudeCounter.exe`** (existing tray) - the control panel. Writes `backup.json`,
  registers/updates the scheduled task, and offers "Back up now". Feature-detects
  the worker: if `ClaudeBackup.exe` is not present next to it, the Backup tab and
  menu item are hidden entirely.
- **`ClaudeBackup.exe`** (new console project `src/ClaudeBackup`) - the worker.
  Reads `backup.json`, runs the selected backends, logs, and returns an exit code
  for Task Scheduler. Runs with no console window in scheduled mode.

**Shared code**: both reference the existing `ClaudeCounter.Core` types where it
makes sense (`Log`, path helpers). Extract path/settings-location helpers into
`Core` if they currently live in the tray project, rather than duplicating.

### New project

`src/ClaudeBackup/ClaudeBackup.csproj` - `net8.0-windows`, `OutputType=Exe`,
`Version` inherited from `Directory.Build.props`. Added to `ClaudeCounter.sln`.
Subject to the same `TreatWarningsAsErrors=true` and locked package restore.

## Security model (non-negotiable)

- **Hard denylist in code**, applied after include/exclude globs and not
  overridable by `backup.json`:
  - `.credentials.json` (any path segment)
  - anything matching `*token*`, `*secret*`, `*.key`, `*.pem`
  - `session.dat` (encrypted session material referenced by the installer)
- **Pre-flight scan**: before any commit/upload, the staged set is scanned; if a
  denylisted file is present, the run **aborts with a non-zero exit code** and logs
  the offending path. Fail closed.
- GitHub target **must be a private repo**. We cannot always detect visibility, so
  the tray UI states this requirement and the worker logs a warning noting it
  cannot verify privacy.
- No secret is ever written to `backup.json` or the log.

## Configuration

`%APPDATA%\ClaudeCounter\backup.json` (Roaming; covered by the installer's
"also remove settings?" uninstall prompt). Written by the tray, read by the worker.

```jsonc
{
  "sourceRoot": "%USERPROFILE%\\.claude",   // plus ~/.claude.json handled specially
  "include": ["settings.json", "CLAUDE.md", "commands/**", "agents/**",
              "plugins/**/*.json"],
  "exclude": ["projects/**", "statsig/**", "**/*cache*"],
  "github": { "enabled": true,  "remoteUrl": "git@github.com:user/claude-backup.git",
              "branch": "main" },
  "drive":  { "enabled": true,  "rcloneRemote": "gdrive:ClaudeBackups" },
  "schedule": { "frequency": "daily", "time": "09:00" }
}
```

- **Include/exclude** are user-editable (the "let me choose files" decision), with
  the secure defaults above. `projects/` is excluded by default.
- Globs are resolved relative to `sourceRoot`; the denylist is applied last.

## Backends

**GitHub (`GitBackend`)**
- Maintains a staging working tree at `%LOCALAPPDATA%\ClaudeCounter\backup-repo`.
- First run: `git init`, set `remote origin` to `remoteUrl`, create branch.
- Each run: mirror the selected files into the tree (add + delete removed),
  `git add -A`, `git commit -m "Backup <timestamp>"` (skip if no changes),
  `git push origin <branch>`.
- **Auth**: relies on the user's existing Git Credential Manager - no token
  handling in our code. If push auth fails, exit non-zero and log guidance.
- Requires `git` on PATH; detected up front.

**Google Drive (`RcloneBackend`)**
- Zips the selected files to a temp archive
  `%LOCALAPPDATA%\ClaudeCounter\backup-tmp\claude-backup-<timestamp>.zip`.
- `rclone copy <zip> <rcloneRemote>`; deletes the temp zip on success.
- **Auth**: entirely rclone's (`rclone config` done once by the user). We detect
  `rclone` on PATH and error cleanly with setup guidance if missing/unconfigured.

Backends run independently; one failing does not abort the other. The process exit
code is non-zero if **any** enabled backend fails, so Task Scheduler records it.

## Scheduling & tray integration

**Tray "Backup" tab** (new, in `SettingsForm`, shown only when the worker exists):
- Destination config (GitHub remote URL + branch; rclone remote path), each with
  an enable checkbox.
- Include/exclude editor (multiline lists with the defaults pre-filled).
- Frequency (Daily / Weekly / Hourly) + time.
- **"Register scheduled task"** - writes `backup.json`, then shells out to
  `schtasks.exe /Create /F` to create/replace a per-user task
  `ClaudeCounter Backup` running `"{app}\ClaudeBackup.exe"` on the chosen schedule.
- **"Back up now"** - launches `ClaudeBackup.exe` as a child process and surfaces
  the exit result.
- Tray context menu gains **"Back up now"** (also gated on worker presence).

**Worker logging**: same `logs/` folder and `Log` class as the tray, so scheduled
runs are visible in one place. Each run logs start, per-backend result, and the
final exit code.

## Installer changes (`packaging/inno/ClaudeCounter.iss`)

- Add `[Components]` with one optional entry, **unchecked by default**:
  `Name: "backup"; Description: "Backup tools (ClaudeBackup)"`.
- Add a `[Files]` line for `ClaudeBackup.exe`
  (`Source: "{#SourceDir}\ClaudeBackup.exe"; ... Components: backup`).
- Re-running setup and toggling the box adds/removes the worker; the tray
  feature-detects on next launch.
- **winget / silent installs** run with default components, so backup is **off**
  unless `/COMPONENTS="backup"` is passed - matching the opt-in intent.
- Uninstall already clears `{localappdata}\ClaudeCounter` (staging repo, temp zips,
  logs) via the existing `[UninstallDelete]`; `backup.json` in Roaming is covered by
  the existing settings-removal prompt. The scheduled task must be removed on
  uninstall: add `schtasks /Delete /F /TN "ClaudeCounter Backup"` in the
  uninstall `[Code]` step (ignore failure if absent).

## Build / CI changes (`.github/workflows/release.yml`)

- The publish step currently emits only `ClaudeCounter.exe` into
  `dist\win-<arch>`. Add a publish of `src/ClaudeBackup` into the **same** per-arch
  folder so the installer can pick up `ClaudeBackup.exe` for the `backup` component.
- Framework-dependent, single-file, per-arch - matching how the tray exe is built.
- No new NuGet packages expected; if any are added, update `packages.lock.json`
  (locked restore will otherwise fail CI).

## Testing

- **`GlobMatcher` / include-exclude resolution** - unit tests: defaults select the
  expected files; `projects/` excluded by default; user excludes honored.
- **Denylist** - unit tests: `.credentials.json` and token/secret/pem/key patterns
  are removed even when explicitly included; pre-flight scan aborts (non-zero) when
  a denylisted file is force-staged.
- **Config round-trip** - `backup.json` serialize/deserialize with defaults for
  missing keys.
- Backends (`GitBackend`, `RcloneBackend`) isolate their external-process calls
  behind a small `IProcessRunner` so success/failure/ missing-tool paths are
  unit-testable without invoking real git/rclone.
- **Manual** (`docs/MANUAL-TESTING.md`): install with the component on/off and
  confirm the tab appears/hides; register a task and confirm it runs from Task
  Scheduler; verify `.credentials.json` never appears in the repo or zip; verify a
  missing rclone/git produces a clear logged error and non-zero exit.

## Build order

Ship after the alert-popups feature. Suggested internal order:
1. `ClaudeBackup` core (config, glob matcher, denylist, git backend) + tests.
2. rclone backend.
3. Tray Backup tab + `schtasks` registration + feature detection.
4. Installer component + release workflow publish + uninstall task cleanup.

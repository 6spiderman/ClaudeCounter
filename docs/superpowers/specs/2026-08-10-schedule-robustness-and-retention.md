# Schedule Robustness and Drive Retention - Design

## Context

Two gaps found by reading the shipped code:

**Missed runs are silently skipped.** The task is registered with
`schtasks /Create /F /TN "ClaudeCounter Backup" /TR "..." /SC DAILY /ST 09:00`.
Windows' "run as soon as possible after a scheduled start is missed" defaults to
OFF, and `schtasks /Create` exposes no flag for it. So a laptop asleep at 09:00
gets no backup that day and no catch-up - the next attempt is tomorrow. For the
machine this is built for, that is most days.

**Drive backups accumulate forever.** Each run uploads a new
`claude-backup-<timestamp>-<guid>.zip` via `rclone copy`. Nothing is overwritten
and nothing is pruned. `SweepStaleZips` cleans only the LOCAL temp directory; it
never touches the remote. Daily backups mean roughly 365 zips a year, growing
without limit. GitHub has no equivalent problem - each run is a commit, and git
history is the retention model.

## Part 1 - Schedule robustness

`schtasks /Create` with flags cannot express the settings we need. Switch to
`schtasks /Create /XML <file>`, generating the task definition ourselves.

Settings to expose, all on the Backup tab, all defaulting to the safer behaviour:

| Setting | Task XML element | Default | Why |
|---|---|---|---|
| Run a missed backup as soon as possible | `StartWhenAvailable` | **true** | The whole point of this change. |
| Only run when a network is available | `RunOnlyIfNetworkAvailable` | **true** | Both destinations upload; without a network the run fails and burns a retry. |
| Don't start on battery | `DisallowStartIfOnBatteries` | **false** | A laptop is exactly where missed runs happen. Defaulting this true would reintroduce the problem being fixed. |
| Stop if the machine switches to battery | `StopIfGoingOnBatteries` | **false** | Same reasoning; a half-finished git push is worse than a slow one. |
| Retry on failure | `RestartOnFailure` (interval + count) | **true, 15 min x 3** | Transient network and auth-refresh failures are the common case. |

Note the two battery defaults are deliberately the opposite of the Windows
defaults. Windows defaults both to true, which on a laptop means the backup only
runs when plugged in - precisely the silent-skip behaviour this work exists to
remove. Anyone who wants the conservative behaviour can tick them.

The generated XML must be written to a temp file, passed to
`schtasks /Create /F /TN ... /XML <file>`, and the temp file deleted afterwards.
Keep `Unregister()` as it is - `/Delete` needs no XML.

`BuildSchtasksArgs` stays for the `/Delete` and `/Query` paths. Add a pure
`BuildTaskXml(ScheduleConfig, string workerPath)` that is unit-testable without
touching the filesystem or invoking `schtasks` - that is where the real logic now
lives, and it must be tested the way the argument builder is.

## Part 2 - Drive retention

Add to `DriveTarget`:

- `int? KeepLastCount` - keep at most this many backups.
- `int? DeleteOlderThanDays` - delete backups older than this.

Both optional and independently switchable. When both are set, **a zip is pruned
if EITHER rule says so** (the union - whichever removes more), per the user's
choice.

Three safety rules, non-negotiable:

1. **Never prune before a successful upload.** Pruning runs only after `rclone copy`
   returns success, so a failed run can never shrink the backup set.
2. **Never delete the last remaining backup**, whatever the rules say. A pair of
   aggressive settings must not be able to empty the remote.
3. **Never delete anything that is not ours.** Only files matching
   `claude-backup-*.zip` in the configured remote path are candidates. A user
   pointing the remote at a folder with other content must not lose it.

Mechanics: `rclone lsjson <remote>` to list, parse names and modification times,
apply the rules in memory, then `rclone deletefile` per doomed entry. Every
deletion is logged by name. If listing fails, log and skip pruning - a pruning
failure must never fail the backup run, which has already succeeded by then.

## Testing

All of it is pure logic behind the existing `IProcessRunner` seam, so no real
`schtasks` or `rclone` is invoked:

- `BuildTaskXml` emits each setting correctly, and quotes a worker path containing
  spaces. Round-trip the XML through an `XDocument` parse rather than string
  matching, so a malformed document fails.
- Retention selection: keep-last-N alone, older-than-days alone, both together
  taking the union, the last-remaining floor holding under every combination, and
  non-matching filenames never selected.
- A failed upload performs no pruning.
- A failed listing logs and leaves the run successful.

## Constraints

- No new NuGet packages. `TreatWarningsAsErrors=true`. ASCII only.
- Baseline is 477 passing tests; none removed or weakened.
- The Backup tab is 671px against a ~687px budget, and
  `SettingsFormHeightStaysWithinTheDisplayBudget` enforces it. These settings will
  not fit as plain rows. Put them behind an "Advanced..." dialog opened from the
  Backup tab rather than growing the tab - and do not reintroduce a scrollbar.

# Backup Health Visibility - Design

## Context

Backup failure is currently invisible. Verified against the shipped code:

- **Nothing persists a backup result.** There is no status file and no last-run
  record anywhere.
- **The tray and flyout say nothing about backup at all.**
- A manual "Back up now" shows a message box - visible only because the user is
  standing there when it happens.
- A **scheduled** run's outcome exists only as a Task Scheduler exit code and a line
  in the log.

So a scheduled backup that fails is silent. Worse, a backup that **never runs at
all** - the task was never registered, the machine is always asleep at 09:00, the
Drive token expired after Google's 7-day Testing trap - is equally silent.

Silence currently looks identical to success. For a backup tool that is the one
failure mode that must never exist: the user believes they are protected and they
are not.

## Decisions (agreed with the user)

- **Tray badge + flyout line + one popup per failure.** Persistent where you can
  see it, plus a single interruption - not a nag.
- **Staleness counts as a problem**, with a **configurable** threshold.
- **Last run recorded per destination**, so "Drive is fine, GitHub has been broken
  for a week" is answerable.

## Mechanism

The worker and the tray are separate processes, so the worker writes a small status
file and the tray reads it. No IPC.

`%LOCALAPPDATA%\ClaudeCounter\backup-status.json`, holding per destination:

- last attempt time (UTC)
- last **success** time (UTC) - distinct from attempt, and the basis for staleness
- the outcome of the last attempt, and its message
- whether the destination was enabled at the time

Plus the overall exit code and run time.

### Rules for writing it

1. **Written on every terminating path**, not just success: config errors (exit 1),
   backend failures (exit 2), and the fail-closed offender abort. A run that fails
   before doing anything is exactly the run whose failure must be recorded.
   `Program.Main`'s existing catch-all must write it too.
2. **Never contains a credential.** Messages are scrubbed through the existing
   `CredentialScrubber` before being stored, the same as they already are before
   being logged. rclone and git error output can echo a remote spec.
3. **A failed write never fails the run.** Log and continue - a backup that
   succeeded must not be reported as failed because a status file could not be
   written.
4. **Last success is never overwritten by a failure.** A failing run updates the
   attempt time and outcome; the success timestamp only moves forward on success.
   That is what makes staleness meaningful.

## Health states

Computed by a pure function from the status file, the config, and the current time:

| State | Meaning |
|---|---|
| **NotConfigured** | No destination enabled. **Show nothing at all** - never nag a user who does not use backup. |
| **NeverRun** | Configured but no attempt has ever been recorded. |
| **Healthy** | Every enabled destination succeeded within the staleness threshold. |
| **Failed** | The last attempt for an enabled destination failed. |
| **Stale** | No failure, but the newest success for an enabled destination is older than the threshold. |

`Failed` outranks `Stale` when both apply. Per-destination detail is retained so the
UI can say which one is broken.

## Staleness threshold

New setting, in the Advanced dialog beside the schedule options:
`BackupStaleAfterDays`, default **3**, 0 meaning "never warn about staleness".

Three days rather than one: a daily schedule plus a weekend laptop should not cry
wolf, but a genuinely dead backup surfaces within a few days. The default must not
produce a false alarm on ordinary use, or the signal gets ignored - which would
defeat the whole feature.

## Surfacing

**Fix round 1 (Important 4):** a "problem state" for every surfacing purpose below
means `NeverRun`, `Failed`, or `Stale` - not just `Failed`/`Stale` as originally
written here. A backup that never runs at all - the scheduled task never
registered, the machine is always asleep at the scheduled time - sits at `NeverRun`
forever, and that is this spec's own opening scenario for why silence is
unacceptable. `ClaudeCounter.Shared`'s `BackupHealthStateExtensions.WarrantsAttention()`
(added in the S11a task) is the single source of truth for this: it returns true
for `NeverRun`/`Failed`/`Stale` and false for `Healthy`/`NotConfigured`. S11b must
key every surfacing decision below off `WarrantsAttention()`, not off a
hand-rolled `Failed or Stale` check.

**Tray icon** - a small problem marker drawn in a corner of the existing icon when
`WarrantsAttention()` is true (i.e. `NeverRun`, `Failed`, or `Stale`). The icon
already carries a percentage and a colour band; the marker must not make the
number unreadable at 16x16 as well as 32x32. Reuse `IconRenderer`; the marker is
a variant of the existing render, not a new icon pipeline. The tooltip gains a
line naming the problem.

**Flyout** - a line showing backup health, and when there is a problem
(`WarrantsAttention()` true, including `NeverRun`), which destination and when it
last succeeded (or that it has never succeeded, for `NeverRun`). Absent entirely
when `NotConfigured`.

**Popup** - one per transition into a problem state, reusing the existing alert
popup infrastructure so placement and auto-dismiss settings are honoured. It must
**not** re-fire every poll: remember the last state notified, and only notify when
the state changes into one where `WarrantsAttention()` is true from one where it
was false (e.g. Healthy -> Failed, Healthy -> Stale, or NotConfigured/first-ever
poll -> NeverRun). Recovery back to Healthy is worth a quieter acknowledgement -
no popup, just the badge clearing.

Dedupe state persists across restarts, in `AppSettings` alongside the existing alert
dedupe state, so restarting the tray does not re-announce a known failure.

## Testing

The valuable parts are pure and must be testable with no UI and no real processes:

- **Health computation**: every state from a constructed status file plus config,
  including `Failed` outranking `Stale`, `NotConfigured` producing silence, and the
  0-means-never-warn threshold.
- **Staleness boundary**: exactly at the threshold, and one second either side.
- **Last-success preservation**: a failing run after a successful one leaves the
  success timestamp intact - this is the property staleness depends on.
- **Status is written on every exit path**, including config error, offender abort,
  and an exception escaping to `Main`.
- **Scrubbing**: a message containing a credential-shaped URL is stored scrubbed.
- **Notification dedupe**: repeated polls in the same state notify once; a state
  change notifies again; the decision survives a simulated restart.
- Status file round-trips, and a corrupt or missing file degrades to `NeverRun`
  rather than throwing.

## Constraints

- No new NuGet packages. `TreatWarningsAsErrors=true`. ASCII only.
- Baseline is 630 passing tests; none removed or weakened.
- The Backup tab is 671px against a ~687px budget enforced by a test. The staleness
  setting goes in the **Advanced dialog**, not the tab.
- The tray must not depend on `ClaudeBackup.csproj` - status types live in
  `ClaudeCounter.Shared`.

## Build order

1. Status file: model, writing from the worker on every path, health computation.
   All pure, all testable, no UI.
2. Surfacing: tray badge, tooltip, flyout line, popup, dedupe, and the setting.

Step 1 first so the logic that decides "is my backup healthy" is settled and tested
before any pixel depends on it.

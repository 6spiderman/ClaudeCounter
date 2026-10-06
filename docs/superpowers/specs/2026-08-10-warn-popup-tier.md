# Warn-Threshold Popup Tier - Design

## Context

Alerts currently fire at two levels: the critical threshold (default 90%) and 100%.
The warn threshold (default 75%) exists in Settings but only tints the tray icon
amber - it raises no popup. The user asked for a popup at the warn level too, so a
run can be noticed before it is nearly out of budget rather than only at the point
it becomes urgent.

This is not a cosmetic addition. `AlertLevel` is an ordered enum whose numeric
values are persisted in `settings.json`, and the tracker's re-arm heuristic is
written in terms of the warn threshold - so inserting a level between None and
Critical touches both stored state and the reset logic.

## Current behaviour

`ThresholdTracker` (`src/ClaudeCounter/Notifications/ThresholdTracker.cs`):

- `enum AlertLevel { None = 0, Critical = 1, Maxed = 2 }`
- Level for a window: `>= 100` is `Maxed`, `>= CriticalThreshold` is `Critical`,
  otherwise `None`.
- Emits only when `level > state.LastAlertedLevel`, so each crossing alerts once
  and escalation Critical -> Maxed alerts again.
- Re-arms on reset, detected as either a changed `resets_at` OR a "sharp drop":
  `LastAlertedLevel >= Critical && utilization < WarnThreshold`.
- `WindowAlertState.LastAlertedLevel` is serialised as an integer into
  `AppSettings.NotificationState`.

## Design

### 1. Renumber the enum, and migrate stored state

`enum AlertLevel { None = 0, Warn = 1, Critical = 2, Maxed = 3 }`

Ordering must stay meaningful because the whole state machine is `>` comparisons.
That means `Critical` changes from 1 to 2 and `Maxed` from 2 to 3, so a
`settings.json` written by the current release would be misread - a stored `1`
(Critical) would come back as `Warn`, and the user would get a duplicate Critical
popup on first run after upgrading.

Handle it explicitly rather than tolerating a silent misfire: add an integer
`NotificationStateVersion` to `AppSettings` (absent/0 means pre-Warn). On load, if
the version is below the current one, **clear `NotificationState`** and stamp the
new version. The state is only dedupe bookkeeping, so clearing it costs at most one
extra alert per window, and only once. Do not attempt to remap old values - the
mapping is ambiguous once Maxed is involved and the payoff is not worth it.

### 2. Rework the re-arm heuristic

The sharp-drop clause currently uses `WarnThreshold` as the floor, which no longer
works once Warn itself alerts: a window that alerted at Warn and then dropped to 5%
would never re-arm through that clause, because the clause requires
`LastAlertedLevel >= Critical`.

Replace the floor with a level-based rule: a window re-arms when its CURRENT level
is `None` while `LastAlertedLevel` is above `None` - i.e. usage has fallen back
below the lowest alerting threshold. That is simpler than the threshold arithmetic,
covers every tier uniformly, and preserves the existing intent (a dip inside a band
must not re-arm; a genuine collapse must).

Keep the `resets_at` change as the primary signal. Keep the existing regression
test that pins the documented `[Warn, Critical)` gap, updating it if the new rule
changes that case - and say so explicitly rather than quietly deleting it.

### 3. Settings

- New `bool WarnAlertsEnabled` on `AppSettings`, **default false**. Warn popups are
  the most frequent tier, so opting in is the polite default for existing users who
  have not asked for them; a user who wants them ticks the box.
- New checkbox on the Alerts tab, above the critical one, worded to match:
  "Popup when a window hits the warn threshold".
- The per-window checkboxes continue to gate all tiers - they choose which windows
  may alert at all, independent of level.

### 4. Popup styling

Three tiers, visually ordered by severity:

- **Warn** - amber, using `Theme.BandColor(Band.Amber)`, same compact card shape as
  Critical so the difference reads as severity, not layout.
- **Critical** - unchanged compact card.
- **Maxed** - unchanged large red "Time to touch some grass".

Warn follows the same placement setting as Critical, and auto-dismisses the same
way. It must never take focus.

`AlertContent.For` gains a Warn case: title naming the window and level, body with
the percentage and the reset countdown, matching the existing phrasing.

## Testing

`ThresholdTracker` is pure and already well covered; extend it:

- Crossing 75 with warn enabled emits exactly one Warn event.
- Crossing 75 then 90 emits Warn then Critical - two events, not three, and not one.
- Going straight from 50 to 95 in one poll emits Critical only, not Warn then
  Critical.
- 100 after Critical emits Maxed once.
- A dip from 80 to 76 emits nothing (still inside the warn band).
- Dropping below the warn threshold and climbing back re-arms and alerts again.
- With `WarnAlertsEnabled` false, no Warn event is emitted at any utilisation, and
  Critical and Maxed still work.
- A `settings.json` at the old state version has its `NotificationState` cleared and
  the version stamped.

`AlertContent` gains a Warn case test. No `Form` is constructed in any test.

## Constraints

- No new NuGet packages. `TreatWarningsAsErrors=true`. ASCII only.
- Baseline is 401 passing tests; none removed or weakened.
- Land after the Settings tab-layout fix, so the new checkbox is added to a
  container known to lay out correctly.

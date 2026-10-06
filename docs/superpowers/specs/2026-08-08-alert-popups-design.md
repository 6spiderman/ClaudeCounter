# Alert Popups - Design

## Context

ClaudeCounter shows Claude Max usage in the tray. It currently has **no proactive
alerting**: the user must hover or open the flyout to notice they are near a limit.

An earlier toast-notification subsystem (`Microsoft.Toolkit.Uwp.Notifications`,
`ThresholdTracker`, `NotificationState`) was removed in commit `bfd8418` because it
was noisy and the dependency was heavy. This feature reintroduces alerting in a
**scoped, dependency-free** form: custom in-app popup windows that fire only at the
critical threshold and at 100%, once per crossing.

Goal: tell the user the moment a usage window becomes critical, and mark the 100%
moment with a deliberately louder "Time to touch some grass" popup - without
re-adding a NuGet dependency and without spamming a popup every poll.

## Goals / Non-goals

**Goals**
- Custom always-on-top popup at the critical threshold and at 100%.
- Fire once per crossing per window; re-arm only when that window resets.
- Per-window selection of which windows may raise alerts.
- Customizable placement: near the tray (no focus steal) or screen-centered.
- All alerting is toggleable and off-by-default-safe.

**Non-goals**
- No Action Center / Windows toast integration (no `Microsoft.Toolkit`).
- No sound, no snooze scheduling, no history log of past alerts.
- No alerting on the extra-usage/credit window in this version.

## Design

### Components

**`Notifications/ThresholdTracker.cs`** (restored, trimmed from `bfd8418~1`)
- Pure state machine, no UI, no I/O - unit-testable in isolation.
- `Evaluate(UsageSnapshot, AppSettings) -> List<AlertEvent>`.
- Per tracked window key, remembers the last level it alerted at
  (`None` / `Critical` / `Maxed`) and the window's last `resets_at`.
- Emits `AlertEvent` when the level **rises** past a threshold it has not yet
  alerted for. A dip back down does **not** re-arm (avoids boundary ping-pong);
  only a window reset (changed `resets_at`, or a sharp drop below warn) clears
  state so the next crossing alerts again.
- Honors per-window enable flags from settings: a disabled window is skipped.
- State round-trips through `AppSettings.NotificationState` (restored) so a
  restart does not re-pop an alert the user already saw.

**`AlertEvent`** (record)
- `WindowKey`, `WindowLabel`, `Level` (`Critical` | `Maxed`), `Utilization`,
  `ResetsAt`.

**`UI/AlertPopupForm.cs`** (new, WinForms, no dependency)
- Borderless, top-most, rounded-corner window - reuses the `NativeMethods`
  helpers and the no-activate positioning pattern already in `FlyoutForm`.
- Two visual tiers driven by `AlertEvent.Level`:
  - **Critical**: compact card - window label, percentage, "resets in X"
    (via existing `TimeText.Countdown`), a Dismiss button.
  - **Maxed (100%)**: larger, celebratory styling with the headline
    **"Time to touch some grass"** plus the window and reset time.
- **Placement** from `AppSettings.PopupPlacement`:
  - `NearTray`: positioned by the cursor/work-area like the flyout, shown with
    `ShowWithoutActivation` + `WS_EX_NOACTIVATE` so it never steals keyboard
    focus; auto-dismisses after a timeout.
  - `Centered`: screen-centered, takes focus, stays until dismissed.
  - The **Maxed popup is always Centered + focus** regardless of the setting,
    for impact (documented default; overridable later if desired).

### Settings additions (`Settings/AppSettings.cs` + `UI/SettingsForm.cs`)

New "Alerts" group in the existing settings dialog:
- `bool CriticalAlertsEnabled` (default true)
- `bool MaxedAlertsEnabled` (default true)
- Per-window enable flags (default: 5-hour on; weekly/all-models on; Opus and
  Sonnet off) exposed as checkboxes:
  `AlertFiveHour`, `AlertSevenDay`, `AlertSevenDayOpus`, `AlertSevenDaySonnet`.
- `PopupPlacement PopupPlacement` (enum `NearTray` | `Centered`, default
  `NearTray`) as a dropdown.
- `Dictionary<string, WindowAlertState> NotificationState` (restored) - persisted
  dedupe state, not shown in the UI.

`AppSettings.Normalize()` gains no new clamps beyond enum validity. Old
`settings.json` files without these keys deserialize to the defaults above.

### Data flow

`PollingService` (unchanged) -> `TrayApplicationContext.OnPollUpdated(state)`:
1. Existing icon/tooltip/flyout update runs first.
2. If `state.Problem == None` and `state.Snapshot is { } snapshot`:
   `var events = _tracker.Evaluate(snapshot, _settings);`
3. For each event, if the matching master toggle is on, show an
   `AlertPopupForm`. Then persist `NotificationState` via the existing
   `SaveSettings()`.

This is the same wiring block removed in `bfd8418`, re-pointed at
`AlertPopupForm` instead of a toast builder.

### Error handling

- Popup creation is wrapped so a UI failure can never take down the tray
  (matches the old `NotificationService` try/catch discipline). A failure is
  logged via `Log.Warn` and swallowed.
- All popup work happens on the UI thread - `OnPollUpdated` already resumes on
  the WinForms `SynchronizationContext` (see `PollingService` remarks), so no
  `Invoke` marshaling is needed.
- Multiple simultaneous events (e.g. two windows cross at once) show stacked
  popups; near-tray popups offset vertically so they do not fully overlap.

## Testing

- **`ThresholdTrackerTests`** (restored + extended): no-alert below threshold;
  one alert per crossing; escalation Critical -> Maxed emits once; dip below does
  not re-arm; reset re-arms; disabled window emits nothing; state round-trips
  through JSON. This mirrors the deleted test file, plus a `Maxed` case.
- Keep `ThresholdTracker` UI-free so these stay fast, deterministic unit tests.
- **Manual** (add to `docs/MANUAL-TESTING.md`): force a window near threshold and
  confirm one popup, correct tier, correct placement, no re-pop on the next poll,
  and re-arm after a simulated reset. Verify near-tray placement does not steal
  focus from the active app.

## Constraints

- No new NuGet packages (respects the `bfd8418` cleanup and locked restore).
- `TreatWarningsAsErrors=true` - new code must be warning-clean.
- Windows tooltip/popup text stays ASCII-dash only (repo style: no em-dashes).

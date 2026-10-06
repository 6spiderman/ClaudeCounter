# Alert Popups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a custom, dependency-free popup when a usage window crosses the critical threshold or hits 100%, once per crossing, with per-window selection and configurable placement.

**Architecture:** A pure `ThresholdTracker` state machine (restored and trimmed from commit `bfd8418~1`) turns each successful poll into at-most-one `AlertEvent` per window per crossing. `TrayApplicationContext` shows an `AlertPopupForm` per event. Dedupe state persists in `settings.json` so restarts do not re-pop.

**Tech Stack:** C# / .NET 8 WinForms, xUnit. No new NuGet packages.

## Global Constraints

- Target framework: `net8.0-windows10.0.17763.0` (unchanged).
- No new NuGet packages (the toast dependency was deliberately removed in `bfd8418`).
- `TreatWarningsAsErrors=true` (Directory.Build.props) - all code must be warning-clean.
- Locked restore: no dependency changes, so `packages.lock.json` stays untouched.
- Text is ASCII only - plain dashes `-`, never em-dashes.
- `PollingService`/`OnPollUpdated` already resume on the WinForms `SynchronizationContext`; all popup work runs on the UI thread with no `Invoke`.

## File Structure

- Create `src/ClaudeCounter/Notifications/ThresholdTracker.cs` - alert types + state machine (pure, no UI/IO).
- Create `src/ClaudeCounter/UI/AlertPopupForm.cs` - the popup window + a pure `AlertContent` helper.
- Modify `src/ClaudeCounter/NativeMethods.cs` - add `WS_EX_NOACTIVATE`.
- Modify `src/ClaudeCounter/Settings/AppSettings.cs` - alert toggles, per-window flags, placement enum, restored `NotificationState`.
- Modify `src/ClaudeCounter/TrayApplicationContext.cs` - wire tracker + popups into `OnPollUpdated`.
- Modify `src/ClaudeCounter/UI/SettingsForm.cs` - "Alerts" settings group.
- Create `tests/ClaudeCounter.Tests/ThresholdTrackerTests.cs` - tracker unit tests.
- Create `tests/ClaudeCounter.Tests/AlertContentTests.cs` - popup content helper tests.
- Modify `tests/ClaudeCounter.Tests/UsageModelsTests.cs` only if a shared `Snap` helper is needed (it is not; tracker tests define their own).

---

### Task 1: ThresholdTracker state machine + alert types

**Files:**
- Create: `src/ClaudeCounter/Notifications/ThresholdTracker.cs`
- Test: `tests/ClaudeCounter.Tests/ThresholdTrackerTests.cs`

**Interfaces:**
- Consumes: `ClaudeCounter.Core.UsageSnapshot`, `UsageWindow` (existing); `ClaudeCounter.Settings.AppSettings` (extended in Task 2, but Task 1 only reads `CriticalThreshold` which already exists, plus the per-window flags added in Task 2 - see note below).
- Produces:
  - `enum AlertLevel { None = 0, Critical = 1, Maxed = 2 }`
  - `sealed record AlertEvent(string WindowKey, string WindowLabel, AlertLevel Level, double Utilization, DateTimeOffset? ResetsAt)`
  - `sealed class WindowAlertState { AlertLevel LastAlertedLevel; DateTimeOffset? LastResetsAt; }`
  - `sealed class ThresholdTracker` with `Dictionary<string,WindowAlertState> State { get; }` and `List<AlertEvent> Evaluate(UsageSnapshot snapshot, AppSettings settings)`

> **Ordering note:** Task 1 and Task 2 are interdependent (the tracker reads per-window flags added in Task 2). Implement Task 2's `AppSettings` fields first if your compiler complains, or stub the four `bool` properties on `AppSettings` at the start of Task 1 and let Task 2 formalize them. The plan lists the tracker first because it is the core logic under test.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/ClaudeCounter.Tests/ThresholdTrackerTests.cs
using ClaudeCounter.Core;
using ClaudeCounter.Notifications;
using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

public class ThresholdTrackerTests
{
    private static readonly DateTimeOffset R1 = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset R2 = new(2026, 8, 8, 17, 0, 0, TimeSpan.Zero);

    // All windows enabled so tests exercise the tracker, not the filter.
    private static AppSettings Settings() => new()
    {
        CriticalThreshold = 90,
        AlertFiveHour = true, AlertSevenDay = true,
        AlertSevenDayOpus = true, AlertSevenDaySonnet = true,
    };

    private static UsageSnapshot FiveHour(double util, DateTimeOffset? reset = null) =>
        new(new UsageWindow(util, reset ?? R1), null, null, null, null);

    [Fact]
    public void BelowCriticalEmitsNothing()
    {
        var t = new ThresholdTracker();
        Assert.Empty(t.Evaluate(FiveHour(50), Settings()));
    }

    [Fact]
    public void CriticalCrossingEmitsOnce()
    {
        var t = new ThresholdTracker();
        var e = Assert.Single(t.Evaluate(FiveHour(91), Settings()));
        Assert.Equal(AlertLevel.Critical, e.Level);
        Assert.Equal("five_hour", e.WindowKey);
        Assert.Empty(t.Evaluate(FiveHour(93), Settings())); // still critical, no repeat
    }

    [Fact]
    public void MaxedEmitsOnceAndEscalatesFromCritical()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91), Settings());
        var e = Assert.Single(t.Evaluate(FiveHour(100), Settings()));
        Assert.Equal(AlertLevel.Maxed, e.Level);
        Assert.Empty(t.Evaluate(FiveHour(100), Settings()));
    }

    [Fact]
    public void DipBelowDoesNotReArm()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91), Settings());
        Assert.Empty(t.Evaluate(FiveHour(80), Settings())); // dropped but same window
        Assert.Empty(t.Evaluate(FiveHour(91), Settings())); // back up, no repeat
    }

    [Fact]
    public void WindowResetReArms()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91, R1), Settings());
        Assert.Empty(t.Evaluate(FiveHour(5, R2), Settings())); // reset, back to normal
        var e = Assert.Single(t.Evaluate(FiveHour(91, R2), Settings()));
        Assert.Equal(AlertLevel.Critical, e.Level);
    }

    [Fact]
    public void SharpDropCountsAsResetWhenResetsAtUnchanged()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(95, R1), Settings());
        Assert.Empty(t.Evaluate(FiveHour(5, R1), Settings())); // collapse, same resets_at
        Assert.Single(t.Evaluate(FiveHour(95, R1), Settings())); // re-armed
    }

    [Fact]
    public void DisabledWindowEmitsNothing()
    {
        var t = new ThresholdTracker();
        var s = Settings();
        s.AlertFiveHour = false;
        Assert.Empty(t.Evaluate(FiveHour(100), s));
    }

    [Fact]
    public void NullResetsAtIsHandled()
    {
        var t = new ThresholdTracker();
        var snap = new UsageSnapshot(null, null, null,
            new UsageWindow(100, null), null); // sonnet maxed, null resets_at
        var e = Assert.Single(t.Evaluate(snap, Settings()));
        Assert.Equal("seven_day_sonnet", e.WindowKey);
        Assert.Null(e.ResetsAt);
    }

    [Fact]
    public void StateRoundTripsThroughJson()
    {
        var t = new ThresholdTracker();
        t.Evaluate(FiveHour(91), Settings());
        var json = System.Text.Json.JsonSerializer.Serialize(t.State);
        var restored = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, WindowAlertState>>(json)!;
        var resumed = new ThresholdTracker(restored);
        Assert.Empty(resumed.Evaluate(FiveHour(91), Settings())); // remembers it alerted
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter ThresholdTrackerTests`
Expected: FAIL - `ThresholdTracker` / `AlertEvent` do not exist.

- [ ] **Step 3: Implement the tracker**

```csharp
// src/ClaudeCounter/Notifications/ThresholdTracker.cs
using ClaudeCounter.Core;
using ClaudeCounter.Settings;

namespace ClaudeCounter.Notifications;

public enum AlertLevel { None = 0, Critical = 1, Maxed = 2 }

public sealed class WindowAlertState
{
    public AlertLevel LastAlertedLevel { get; set; }
    public DateTimeOffset? LastResetsAt { get; set; }
}

public sealed record AlertEvent(
    string WindowKey, string WindowLabel, AlertLevel Level,
    double Utilization, DateTimeOffset? ResetsAt);

/// <summary>
/// Pure state machine: one alert per threshold crossing per window. A dip below
/// a threshold does NOT re-arm (no boundary ping-pong); only a window reset
/// (changed resets_at, or a sharp drop below critical) clears state so the next
/// crossing alerts again. No UI, no IO - unit-tested in isolation.
/// </summary>
public sealed class ThresholdTracker
{
    private readonly Dictionary<string, WindowAlertState> _state;

    public ThresholdTracker(Dictionary<string, WindowAlertState>? initialState = null) =>
        _state = initialState ?? new Dictionary<string, WindowAlertState>();

    public Dictionary<string, WindowAlertState> State => _state;

    public List<AlertEvent> Evaluate(UsageSnapshot snapshot, AppSettings settings)
    {
        var events = new List<AlertEvent>();
        Eval("five_hour", "5-hour session", snapshot.FiveHour, settings.AlertFiveHour, settings, events);
        Eval("seven_day", "Weekly (all models)", snapshot.SevenDay, settings.AlertSevenDay, settings, events);
        Eval("seven_day_opus", "Weekly (Opus)", snapshot.SevenDayOpus, settings.AlertSevenDayOpus, settings, events);
        Eval("seven_day_sonnet", "Weekly (Sonnet)", snapshot.SevenDaySonnet, settings.AlertSevenDaySonnet, settings, events);
        return events;
    }

    private void Eval(string key, string label, UsageWindow? window, bool enabled,
        AppSettings settings, List<AlertEvent> events)
    {
        if (window is null || !enabled)
            return;

        if (!_state.TryGetValue(key, out var state))
            _state[key] = state = new WindowAlertState();

        // Reset detection: resets_at changed, or a sharp drop below critical
        // (covers windows whose resets_at does not change on rollover, and the
        // null-resets_at case where equality never changes).
        var resetsAtChanged = state.LastResetsAt != window.ResetsAt;
        var sharpDrop = state.LastAlertedLevel >= AlertLevel.Critical
                        && window.Utilization < settings.CriticalThreshold;
        if (resetsAtChanged || sharpDrop)
            state.LastAlertedLevel = AlertLevel.None;
        state.LastResetsAt = window.ResetsAt;

        var level = window.Utilization >= 100 ? AlertLevel.Maxed
            : window.Utilization >= settings.CriticalThreshold ? AlertLevel.Critical
            : AlertLevel.None;

        if (level > state.LastAlertedLevel)
        {
            events.Add(new AlertEvent(key, label, level, window.Utilization, window.ResetsAt));
            state.LastAlertedLevel = level;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter ThresholdTrackerTests`
Expected: PASS (all 9).

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeCounter/Notifications/ThresholdTracker.cs tests/ClaudeCounter.Tests/ThresholdTrackerTests.cs
git commit -m "Add ThresholdTracker alert state machine"
```

---

### Task 2: AppSettings alert fields + placement enum

**Files:**
- Modify: `src/ClaudeCounter/Settings/AppSettings.cs`
- Test: `tests/ClaudeCounter.Tests/UsageModelsTests.cs` is unaffected; add settings assertions to a new region in `ThresholdTrackerTests` is unnecessary. Add a focused test file below.
- Test: `tests/ClaudeCounter.Tests/AppSettingsTests.cs` (create)

**Interfaces:**
- Consumes: nothing new.
- Produces: on `AppSettings` - `bool CriticalAlertsEnabled`, `bool MaxedAlertsEnabled`, `bool AlertFiveHour`, `bool AlertSevenDay`, `bool AlertSevenDayOpus`, `bool AlertSevenDaySonnet`, `PopupPlacement PopupPlacement`, `Dictionary<string,WindowAlertState> NotificationState`; `enum PopupPlacement { NearTray, Centered }`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClaudeCounter.Tests/AppSettingsTests.cs
using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultsAreSafe()
    {
        var s = new AppSettings();
        Assert.True(s.CriticalAlertsEnabled);
        Assert.True(s.MaxedAlertsEnabled);
        Assert.True(s.AlertFiveHour);
        Assert.True(s.AlertSevenDay);
        Assert.False(s.AlertSevenDayOpus);
        Assert.False(s.AlertSevenDaySonnet);
        Assert.Equal(PopupPlacement.NearTray, s.PopupPlacement);
        Assert.NotNull(s.NotificationState);
    }

    [Fact]
    public void NormalizeKeepsExistingBehavior()
    {
        var s = new AppSettings { WarnThreshold = 95, CriticalThreshold = 90 };
        s.Normalize(); // warn >= critical resets both to defaults
        Assert.Equal(75, s.WarnThreshold);
        Assert.Equal(90, s.CriticalThreshold);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter AppSettingsTests`
Expected: FAIL - new properties/enum do not exist.

- [ ] **Step 3: Implement the additions**

Add to `AppSettings.cs` (keep existing members; add `using ClaudeCounter.Notifications;` at top for `WindowAlertState`):

```csharp
public enum PopupPlacement { NearTray, Centered }
```

Inside `AppSettings` (after `AutostartEnabled`):

```csharp
    public bool CriticalAlertsEnabled { get; set; } = true;
    public bool MaxedAlertsEnabled { get; set; } = true;
    public bool AlertFiveHour { get; set; } = true;
    public bool AlertSevenDay { get; set; } = true;
    public bool AlertSevenDayOpus { get; set; }        // default false
    public bool AlertSevenDaySonnet { get; set; }      // default false
    public PopupPlacement PopupPlacement { get; set; } = PopupPlacement.NearTray;

    // Persisted alert dedupe state so restarts do not re-pop.
    public Dictionary<string, WindowAlertState> NotificationState { get; set; } = new();
```

`Normalize()` needs no new logic (bools and a validated enum). Leave it as-is.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter AppSettingsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeCounter/Settings/AppSettings.cs tests/ClaudeCounter.Tests/AppSettingsTests.cs
git commit -m "Add alert settings fields and popup placement"
```

---

### Task 3: AlertPopupForm + AlertContent helper

**Files:**
- Modify: `src/ClaudeCounter/NativeMethods.cs` (add `WS_EX_NOACTIVATE`)
- Create: `src/ClaudeCounter/UI/AlertPopupForm.cs`
- Test: `tests/ClaudeCounter.Tests/AlertContentTests.cs`

**Interfaces:**
- Consumes: `AlertEvent`, `AlertLevel` (Task 1); `TimeText.Countdown` (existing); `PopupPlacement` (Task 2); `NativeMethods.WS_EX_TOOLWINDOW` (existing).
- Produces:
  - `static class AlertContent` with `static (string Title, string Body) For(AlertEvent e, DateTimeOffset now)` - the pure, testable text builder.
  - `sealed class AlertPopupForm : Form` with `static void Show(AlertEvent e, PopupPlacement placement, Point trayAnchor)`.

- [ ] **Step 1: Write the failing test (pure content helper only; the Form is verified manually)**

```csharp
// tests/ClaudeCounter.Tests/AlertContentTests.cs
using ClaudeCounter.Notifications;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

public class AlertContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CriticalMentionsWindowAndPercent()
    {
        var e = new AlertEvent("five_hour", "5-hour session", AlertLevel.Critical, 92,
            Now.AddHours(2));
        var (title, body) = AlertContent.For(e, Now);
        Assert.Contains("5-hour session", title);
        Assert.Contains("92%", body);
        Assert.Contains("resets in", body);
    }

    [Fact]
    public void MaxedSaysTouchGrass()
    {
        var e = new AlertEvent("five_hour", "5-hour session", AlertLevel.Maxed, 100, null);
        var (title, _) = AlertContent.For(e, Now);
        Assert.Contains("touch some grass", title, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullResetsAtOmitsCountdown()
    {
        var e = new AlertEvent("seven_day_sonnet", "Weekly (Sonnet)", AlertLevel.Critical, 91, null);
        var (_, body) = AlertContent.For(e, Now);
        Assert.DoesNotContain("resets in", body);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter AlertContentTests`
Expected: FAIL - `AlertContent` does not exist.

- [ ] **Step 3a: Add the native constant**

In `src/ClaudeCounter/NativeMethods.cs`, add next to `WS_EX_TOOLWINDOW`:

```csharp
    public const int WS_EX_NOACTIVATE = 0x08000000;
```

- [ ] **Step 3b: Implement AlertContent + AlertPopupForm**

```csharp
// src/ClaudeCounter/UI/AlertPopupForm.cs
using ClaudeCounter.Core;
using ClaudeCounter.Notifications;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public static class AlertContent
{
    public static (string Title, string Body) For(AlertEvent e, DateTimeOffset now)
    {
        var reset = e.ResetsAt is { } at ? $"Resets in {TimeText.Countdown(at, now)}." : "";
        if (e.Level == AlertLevel.Maxed)
            return ("Time to touch some grass",
                    $"{e.WindowLabel} is at 100%. {reset}".TrimEnd());
        return ($"{e.WindowLabel} usage critical",
                $"At {e.Utilization:0}%. {reset}".TrimEnd());
    }
}

public sealed class AlertPopupForm : Form
{
    private const int Width_ = 300;
    private static readonly TimeSpan AutoDismiss = TimeSpan.FromSeconds(12);

    private readonly bool _noActivate;
    private readonly System.Windows.Forms.Timer? _dismissTimer;

    private AlertPopupForm(AlertEvent e, PopupPlacement placement, Point trayAnchor)
    {
        // 100% always takes the center + focus; critical follows the setting.
        var centered = placement == PopupPlacement.Centered || e.Level == AlertLevel.Maxed;
        _noActivate = !centered;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Width = Width_;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.Manual;

        var palette = Theme.Current();
        BackColor = e.Level == AlertLevel.Maxed
            ? Theme.BandColor(Band.Red) : palette.Back;

        var (title, body) = AlertContent.For(e, DateTimeOffset.Now);
        BuildContent(title, body, e.Level, palette);
        Place(centered, trayAnchor);

        if (_noActivate)
        {
            _dismissTimer = new System.Windows.Forms.Timer { Interval = (int)AutoDismiss.TotalMilliseconds };
            _dismissTimer.Tick += (_, _) => Close();
            _dismissTimer.Start();
        }
    }

    /// <summary>Show a popup for one alert event. Call on the UI thread.</summary>
    public static void Show(AlertEvent e, PopupPlacement placement, Point trayAnchor)
    {
        var form = new AlertPopupForm(e, placement, trayAnchor);
        if (form._noActivate)
            form.Show();      // ShowWithoutActivation keeps focus with the active app
        else
        {
            form.Show();
            form.Activate();
        }
    }

    // Do not steal focus for near-tray popups.
    protected override bool ShowWithoutActivation => _noActivate;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            if (_noActivate)
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.TryRoundCorners(Handle);
    }

    private void BuildContent(string title, string body, AlertLevel level, Palette palette)
    {
        var fore = level == AlertLevel.Maxed ? Color.White : palette.Fore;
        var pad = 14;
        var titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", level == AlertLevel.Maxed ? 13f : 10.5f),
            ForeColor = fore, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(Width - pad * 2, 0), Location = new Point(pad, pad),
        };
        Controls.Add(titleLabel);

        var bodyLabel = new Label
        {
            Text = body, ForeColor = fore, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(Width - pad * 2, 0),
            Location = new Point(pad, titleLabel.Bottom + 6),
        };
        Controls.Add(bodyLabel);

        var dismiss = new Button
        {
            Text = "Dismiss", FlatStyle = FlatStyle.Flat, ForeColor = fore,
            BackColor = level == AlertLevel.Maxed ? Color.FromArgb(60, 0, 0, 0) : palette.BarBack,
            Size = new Size(80, 28),
            Location = new Point(Width - pad - 80, bodyLabel.Bottom + 10),
        };
        dismiss.Click += (_, _) => Close();
        Controls.Add(dismiss);
        Height = dismiss.Bottom + pad;
    }

    private void Place(bool centered, Point trayAnchor)
    {
        var area = Screen.FromPoint(trayAnchor).WorkingArea;
        if (centered)
        {
            var full = Screen.FromPoint(trayAnchor).Bounds;
            Location = new Point(full.Left + (full.Width - Width) / 2,
                                 full.Top + (full.Height - Height) / 2);
        }
        else
        {
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dismissTimer?.Dispose();
        base.Dispose(disposing);
    }
}
```

> If `Theme.Palette` field names (`Back`, `Fore`, `BarBack`, `Border`) differ from those used in `FlyoutForm.cs`, match `FlyoutForm`'s usage exactly - it is the reference for the palette API.

- [ ] **Step 4: Run tests + build**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter AlertContentTests`
Expected: PASS.
Run: `dotnet build ClaudeCounter.sln -c Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeCounter/NativeMethods.cs src/ClaudeCounter/UI/AlertPopupForm.cs tests/ClaudeCounter.Tests/AlertContentTests.cs
git commit -m "Add AlertPopupForm and content helper"
```

---

### Task 4: Wire tracker + popups into the tray

**Files:**
- Modify: `src/ClaudeCounter/TrayApplicationContext.cs`

**Interfaces:**
- Consumes: `ThresholdTracker`, `AlertEvent`, `AlertLevel` (Task 1); `AlertPopupForm.Show` (Task 3); `AppSettings.NotificationState`, master toggles, `PopupPlacement` (Task 2).
- Produces: no new public surface; behavior only.

- [ ] **Step 1: Add the tracker field and construct it**

Add `using ClaudeCounter.Notifications;` at the top. Add a field near the other services:

```csharp
    private readonly ThresholdTracker _tracker;
```

In the constructor, after `_settings` is loaded and before `_polling` is created:

```csharp
        _tracker = new ThresholdTracker(_settings.NotificationState);
```

- [ ] **Step 2: Show popups in OnPollUpdated**

Replace the body of `OnPollUpdated` with:

```csharp
    private void OnPollUpdated(PollState state)
    {
        UpdateIcon(state);
        var tooltip = BuildTooltip(state);
        _notifyIcon.Text = tooltip;
        _flyout.UpdateState(state);
        Log.Info($"Tooltip: {tooltip.Replace("\n", " | ")}");

        if (state.Problem != ProblemKind.None || state.Snapshot is not { } snapshot)
            return;

        var events = _tracker.Evaluate(snapshot, _settings);
        var shown = false;
        foreach (var e in events)
        {
            var enabled = e.Level == AlertLevel.Maxed
                ? _settings.MaxedAlertsEnabled
                : _settings.CriticalAlertsEnabled;
            if (!enabled)
                continue;
            AlertPopupForm.Show(e, _settings.PopupPlacement, GetTrayAnchor());
            shown = true;
        }
        if (events.Count > 0)
            SaveSettings(); // persist dedupe state whether or not a popup showed
        _ = shown;
    }

    private static Point GetTrayAnchor() => Cursor.Position;
```

> `SaveSettings()` already persists `_settings`. Ensure it writes `NotificationState` too: since `_tracker` was constructed from `_settings.NotificationState` (the same dictionary reference), the state is already current on `_settings`. No extra assignment needed.

- [ ] **Step 3: Build and run tests**

Run: `dotnet build ClaudeCounter.sln -c Release && dotnet test ClaudeCounter.sln -c Release --no-build`
Expected: Build 0 warnings; all tests pass.

- [ ] **Step 4: Manual smoke test**

Temporarily set `CriticalThreshold` low (e.g. via `settings.json`) so a normal poll crosses it, run the app, and confirm exactly one popup appears, then none on the next poll. Restore the threshold. (This is documented, not automated.)

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeCounter/TrayApplicationContext.cs
git commit -m "Show alert popups on threshold crossings"
```

---

### Task 5: Settings UI - "Alerts" group

**Files:**
- Modify: `src/ClaudeCounter/UI/SettingsForm.cs`

**Interfaces:**
- Consumes: `AppSettings` alert fields + `PopupPlacement` (Task 2).
- Produces: no new public API beyond the existing `ApplyTo(AppSettings)` contract, which now also writes the alert fields.

- [ ] **Step 1: Add controls**

Grow the dialog and `TableLayoutPanel` row count. Add fields to the class:

```csharp
    private readonly CheckBox _criticalAlerts;
    private readonly CheckBox _maxedAlerts;
    private readonly CheckBox _alertFiveHour;
    private readonly CheckBox _alertSevenDay;
    private readonly CheckBox _alertOpus;
    private readonly CheckBox _alertSonnet;
    private readonly ComboBox _placement;
```

Construct them from `current` (e.g. `_criticalAlerts = new CheckBox { Text = "Alert at critical threshold", Checked = current.CriticalAlertsEnabled, AutoSize = true };`), add them to the layout under an "Alerts" label row, and populate `_placement` with `Near tray` / `Centered` selecting `current.PopupPlacement`. Increase `ClientSize` height to fit (approximately +180px).

- [ ] **Step 2: Persist in ApplyTo**

Extend `ApplyTo`:

```csharp
        settings.CriticalAlertsEnabled = _criticalAlerts.Checked;
        settings.MaxedAlertsEnabled = _maxedAlerts.Checked;
        settings.AlertFiveHour = _alertFiveHour.Checked;
        settings.AlertSevenDay = _alertSevenDay.Checked;
        settings.AlertSevenDayOpus = _alertOpus.Checked;
        settings.AlertSevenDaySonnet = _alertSonnet.Checked;
        settings.PopupPlacement = _placement.SelectedIndex == 1
            ? PopupPlacement.Centered : PopupPlacement.NearTray;
```

- [ ] **Step 3: Build**

Run: `dotnet build ClaudeCounter.sln -c Release`
Expected: 0 warnings.

- [ ] **Step 4: Manual check**

Open Settings, toggle alert options, click OK, reopen - confirm values persisted to `settings.json`.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeCounter/UI/SettingsForm.cs
git commit -m "Add Alerts settings group"
```

---

## Self-Review

- **Spec coverage:** dedupe tracker (Task 1), per-window selection + placement + toggles (Task 2, 5), custom popup with two tiers + no-focus near-tray + centered 100% (Task 3), wiring + persistence (Task 4), settings UI (Task 5). No new package (constraint honored). All spec sections map to a task.
- **Placeholder scan:** none - every code step has full code.
- **Type consistency:** `AlertEvent`, `AlertLevel`, `WindowAlertState`, `PopupPlacement`, `AlertContent.For`, `AlertPopupForm.Show`, `AppSettings.NotificationState` are used with identical names/signatures across tasks.
- **Note carried to execution:** confirm `Theme.Palette` member names against `FlyoutForm.cs` in Task 3.

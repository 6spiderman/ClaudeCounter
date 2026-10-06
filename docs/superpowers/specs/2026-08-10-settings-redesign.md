# Settings Redesign - Design

## Context

The Settings dialog has grown into a single 13-row column roughly 860px tall. Its
height is clamped to the working area minus 80px with `AutoScroll = true`, so on a
normal display it always shows a scrollbar. Every feature added so far has made the
list longer, and backup added nine more controls. The user reports it "looks very
old" and has "an unnecessary scroll".

Separately, the Backup group gives no guidance: it asks for a Remote URL, a branch,
an rclone remote, and include/exclude globs with no explanation of how to obtain
them, what gets backed up, or what is deliberately excluded. A user cannot safely
configure it from the dialog alone.

Goal: restructure Settings into themed tabs so no page scrolls, and make the backup
options self-explanatory in place.

## Decisions (agreed with the user)

- **Tabs**, not a longer page or a nav sidebar.
- **ⓘ icons per field plus one Help button** opening a fuller guide.
- **Modern custom styling** themed to the app's existing light/dark palette, rather
  than plain native controls.

## Structure

`SettingsForm` becomes a fixed-size dialog (target ~560x620 client, must fit
without scrolling on a 1366x768 display) containing:

- A **custom tab strip** across the top: `General`, `Alerts`, `Backup`. Flat
  buttons with an accent underline on the active tab. A custom strip is preferred
  over `TabControl`, whose client area cannot be themed cleanly.
- A **content panel** hosting one panel per tab, only one visible at a time.
- A **bottom bar** with OK / Cancel, outside the tab area so it never scrolls away.

### Tab contents

**General** - update frequency + the rate-limit note, warn threshold, critical
threshold, start with Windows, check for updates automatically.

**Alerts** - popup at critical threshold, popup at 100%, the four per-window
checkboxes (two columns), popup placement.

**Backup** - shown only when `ClaudeBackup.exe` is present, exactly as today:
GitHub group (enable, Remote URL, Branch), Drive group (enable, Rclone remote),
Include and Exclude multiline boxes, Frequency and Time, then Help / Save and
register schedule / Back up now.

When the worker is absent the Backup tab button is not created at all, and the
dialog shrinks accordingly.

## Theming

Reuse `ClaudeCounter.UI.Theme` - `Theme.Current()` returns a `Palette` of
`Back`, `Fore`, `SubtleFore`, `BarBack`, `Border`, already light/dark aware via the
registry. The flyout is the reference for how these are applied.

Flat controls, no 3D borders, consistent 12px margins, Segoe UI at the existing
sizes, section headers in Segoe UI Semibold. Must remain legible at 100%, 125% and
150% DPI - the app is `PerMonitorV2`.

## Help content

This is the substance of the feature, and the facts below are load-bearing - they
describe what the shipped code actually does.

**What backup does.** Copies a chosen subset of your Claude configuration
(`~/.claude`) to a destination you control. It never touches your Claude account or
usage; it is a file copy.

**GitHub setup.** Create a NEW, EMPTY, PRIVATE repository first. Paste its URL -
SSH (`git@github.com:you/repo.git`) is simplest. Authentication uses your existing
Git Credential Manager; ClaudeCounter never handles, stores, or logs a token, and a
URL with an embedded token is rejected. **The repository must be private, and
ClaudeCounter cannot verify that for you.**

**Google Drive setup.** Install rclone and run `rclone config` once to authorise
Drive. Then give the remote as `remote:folder`, e.g. `gdrive:ClaudeBackups`. Each
run uploads a timestamped zip.

**What is backed up by default** - `settings.json`, `CLAUDE.md`, `commands/`,
`agents/`, and top-level `plugins/*.json`.

**What is excluded by default** - `projects/` (session history, large and
regenerable), `statsig/`, and any `cache/` directory.

**What is NEVER backed up, whatever you configure** - `.credentials.json` (live
OAuth tokens), `session.dat` (this app's encrypted session), `*.key`, `*.pem`,
`*.pfx` and similar, `.env`, `.npmrc`, `.git-credentials`, SSH private keys, and
any file whose name contains `token`, `secret`, `credential` or `apikey`. This list
is enforced in code and cannot be overridden from this dialog or by editing
`backup.json`. Files dropped this way are named in the log so a legitimate file
caught by the name rules is visible rather than silently missing.

**Note on the deep plugin tree.** `plugins/**` is deliberately NOT backed up by
default. Plugin and MCP config files commonly embed inline API keys in
innocuously-named files that a name-based filter cannot catch. Add it knowingly if
you want it.

**Scheduling.** "Save and register schedule" creates a per-user Windows Task
Scheduler task named `ClaudeCounter Backup`. Disabling both destinations and saving
removes it. Uninstalling removes it too.

**Results.** "Back up now" reports one of three outcomes - complete, "not run,
check your settings" (nothing enabled, nothing selected, or a bad setting), or
failed (a destination rejected the upload). Details always go to the log.

## Constraints

- No new NuGet packages. `TreatWarningsAsErrors=true`.
- ASCII only in code and comments. UI strings may use the ⓘ glyph, which is the one
  deliberate exception and must be verified to render at all DPI settings. If that
  cannot be verified (e.g. the implementing session cannot launch the GUI to
  screenshot it at 100/125/150% DPI), the sanctioned fallback is a hand-drawn
  circled "i" - an ellipse plus the plain ASCII letter "i", drawn with GDI+
  primitives already used elsewhere in the dialog - rather than a plain "?"
  button or shipping the glyph unverified. This is what shipped: SettingsForm's
  `InfoButton` draws its own circle instead of using U+24D8, so no ⓘ glyph
  actually appears in any UI string.
- Every existing setting must survive: all `AppSettings` fields currently written by
  `ApplyTo`, and the whole backup config round-trip. Losing one silently is the main
  risk of this change.
- `SettingsForm` must not be constructed in any test - the suite's existing rule.
  Testable logic (help text lookup, tab model, validation predicates) should be
  extracted to pure types that can be tested.
- Baseline is 401 passing tests; none may be removed or weakened.

## Build order

1. Themed tabbed restructure, behaviour unchanged.
2. ⓘ helpers and the Backup help dialog.

Splitting keeps each independently reviewable: the first is "nothing changed except
layout", the second is purely additive.

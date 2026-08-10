# Per-Destination Backup Selection and File Picker - Design

## Context

Backup today has ONE include/exclude glob set shared by both destinations, and the
only way to choose files is to type glob patterns into two multiline text boxes. Two
problems follow from that:

1. A private git repo and a Google Drive zip are different things and reasonably
   want different contents, but they are forced to share one selection.
2. A user has no idea what is actually on disk, how big it is, or what the patterns
   will match. Choosing correctly requires knowing the layout of `~/.claude` by
   heart.

## Measured reality (this machine, 2026-08-10)

`~/.claude` is **7,837 files / 436.8 MB**. The distribution is extremely lopsided:

| Entry | Files | MB |
|---|---|---|
| `projects/` | 622 | 340.7 |
| `skills/` | 4,941 | 44.2 |
| `plugins/` | 1,671 | 42.5 |
| `file-history/` | 447 | 5.1 |
| `telemetry/` | 9 | 2.4 |
| `usage.db` | 1 | 0.6 |
| everything else | ~150 | ~1 |

Three directories are 92% of the files and 98% of the bytes. This drives the design:

- **A tree must lazy-load.** Eagerly enumerating 7,837 files to populate a dialog
  is not acceptable, and `skills/` alone has 4,941.
- **Sizes must be shown.** Nothing about the name `projects/` suggests 341 MB. A
  user picking blind will either upload a third of a gigabyte to Drive by accident
  or avoid everything out of caution.
- **The existing defaults are vindicated** - excluding `projects/` and caches is
  what keeps a backup small, not an arbitrary choice.

Also present and relevant: `.credentials.json` (denylisted), and several
transcript-bearing entries - `file-history/`, `history.jsonl`, `shell-snapshots/`,
`telemetry/`, `projects/`. Those hold what was typed and file contents. They are
excluded today only because they are not in the include list, not because anything
protects them. The picker should say so where it is visible.

Other locations found: `~/.claude.json` (84 KB) and `%LOCALAPPDATA%\Claude` (1 file).
`%APPDATA%\Claude`, `%LOCALAPPDATA%\AnthropicClaude` and a VS Code globalStorage
path were checked and do not exist here, so they are probed but not assumed.

## Decisions (agreed with the user)

- **Known locations**, not a drive sweep and not manual-only. Probe the paths above,
  show what exists, and offer "Add folder..." for anything else.
- **Tree primary, globs visible.** Ticking in the tree is the normal way to choose;
  the resulting include patterns are shown read-only so the mapping is transparent.
- **Fully independent per-destination selections.** GitHub and Drive each own their
  include/exclude set. Schedule stays shared.

## Part 1 - Per-destination selection

### Config shape

Move `Include`/`Exclude` from `BackupConfig` onto `GitTarget` and `DriveTarget`.

Migration, versioned the same way `NotificationStateVersion` is: add
`BackupConfigVersion`. On load, if the version is below current AND legacy
top-level `Include`/`Exclude` are non-empty, copy them into BOTH targets, clear the
legacy fields, stamp the version, and save. Copying into both preserves exactly
today's behaviour for an existing user - nothing they configured changes meaning.

Keep the legacy properties on the type (deserialize-only) so an old file still
parses; do not remove them and break `Load()`.

### Worker

`BackupRunner` currently selects files once and hands the same list to both
backends. It must now select per enabled destination, because the sets differ.

Consequences to handle deliberately:
- The fail-closed `SecretDenylist.Offenders` re-assertion must run on EACH
  destination's selection, not once.
- The withheld-files log line must say which destination it applies to.
- "Nothing selected" becomes per destination: if GitHub selects nothing but Drive
  selects files, that is a GitHub config error, not a whole-run failure. Exit 1
  only when NO enabled destination has anything to upload; otherwise run what can
  run and report per-destination results.

### UI

The Backup tab gets a destination selector - GitHub / Drive - switching which
selection is being edited. The connection fields (remote URL, branch, rclone
remote) stay with their own destination.

## Part 2 - File picker

### Scanning

A `ClaudeLocationScanner` in the shared project probes the known paths, returns
those that exist with file count and total size, and never throws on an
inaccessible directory. Probing is cheap - existence plus a lazy size query - and
must not enumerate 7,837 files up front.

### Tree

A `TreeView` with checkboxes, lazy-populated: a node's children are enumerated on
first expand, never before. Each node shows name, and for directories a file count
and size. Sizes are computed on expand, off the UI thread, with a placeholder until
they arrive - `skills/` at 4,941 files must not freeze the dialog.

Tri-state behaviour: ticking a directory selects it wholly (`dir/**`); ticking some
children leaves the parent visually indeterminate and emits the individual paths.

**Denylisted entries are shown, greyed, and cannot be ticked**, with the reason on
hover ("never backed up: credential material"). Showing them greyed rather than
hiding them is deliberate - a user who cannot find `.credentials.json` may assume
the picker is broken, or worse, go and add it to `backup.json` by hand.

**Transcript-bearing entries** (`projects/`, `file-history/`, `history.jsonl`,
`shell-snapshots/`, `telemetry/`) are selectable but carry a visible note that they
contain conversation content and file contents. They stay unticked by default.

### Mapping to patterns

Ticked directory -> `relativePath/**`. Ticked individual file -> its literal
relative path. The generated patterns are shown in a read-only Advanced box beneath
the tree, so the translation is never a mystery.

Loading an existing config back into the tree is best-effort: exact-directory and
literal-file patterns are re-selected; anything more exotic (a hand-written
`**/*.md`) is preserved in the config and shown in the Advanced box as
"not representable in the tree", rather than being silently dropped. **A pattern the
tree cannot represent must never be lost by opening the dialog.** That is the main
correctness risk of this feature.

## Testing

Pure and testable, no `Form` construction:
- Scanner: returns only paths that exist; tolerates a missing/denied directory.
- Pattern mapping: directory -> `dir/**`, file -> literal, both directions.
- Round-trip: config -> tree model -> config is lossless for representable
  patterns, and preserves non-representable ones untouched.
- Denylisted nodes are marked unselectable.
- Per-destination migration: legacy top-level globs land in both targets, version
  stamped, legacy cleared, and a second load does not re-migrate.
- Per-destination runner: one destination with an empty selection does not fail the
  other; exit 1 only when nothing anywhere is selected.

The dialog itself is covered by the existing STA smoke tests, which must be
extended to construct the Settings dialog with a populated tree.

## Build order

1. **Part 1** - per-destination config, migration, runner, and the destination
   selector in the UI. Behaviour-preserving for existing users.
2. **Part 2** - scanner, tree, pattern mapping.

Part 1 first because the tree edits a per-destination selection; building the tree
against the shared model would mean rewriting it immediately.

## Constraints

- No new NuGet packages. `TreatWarningsAsErrors=true`. ASCII only.
- Baseline is 438 passing tests; none removed or weakened.
- The secret denylist stays non-overridable, and the picker must not offer any
  route around it.

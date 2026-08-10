using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// One row of RestoreDialog's preview grid (design spec step 4/5): pairs a
/// classified file with whether it can ever be ticked and whether it starts
/// ticked. No WinForms dependency - RestoreDialog is a thin adapter over
/// this, exactly as BackupPickerDialog is over BackupTreeModel.
/// </summary>
public sealed record RestoreFileRow(RestoreFileEntry Entry, bool Selectable, bool DefaultChecked);

/// <summary>
/// Pure mapping from RestoreClassifier's output to what RestoreDialog's
/// preview grid should show and default-select (design spec step 5): "all
/// New and Changed, nothing else". No WinForms dependency, so this is
/// unit-tested directly rather than only through a Form.
///
/// This is also where restore rule 3 ("restore never deletes") becomes a UI
/// guarantee rather than just an engine one: <see cref="IsSelectable"/> is
/// the single choke point RestoreDialog consults before ever letting a row's
/// checkbox be ticked, so a LiveOnly (or Identical) row can never end up in
/// the set passed to RestoreApplier.Apply, however the dialog's own event
/// wiring is written.
/// </summary>
public static class RestoreDisplayModel
{
    /// <summary>Builds one display row per classified entry, preserving Classify's own ordering.</summary>
    public static IReadOnlyList<RestoreFileRow> BuildRows(IReadOnlyList<RestoreFileEntry> entries) =>
        entries.Select(e => new RestoreFileRow(e, IsSelectable(e.Status), DefaultChecked: IsSelectable(e.Status))).ToList();

    /// <summary>
    /// True only for New and Changed - the two statuses RestoreApplier.Apply
    /// ever actually writes. Identical (nothing to do) and LiveOnly (restore
    /// rule 3: present live, absent from the backup, always left alone) are
    /// never selectable, so the dialog has no way to construct an apply set
    /// containing either one.
    /// </summary>
    public static bool IsSelectable(RestoreFileStatus status) =>
        status is RestoreFileStatus.New or RestoreFileStatus.Changed;

    /// <summary>
    /// Summarises how many rows fall into each status - what the preview
    /// panel's counts (and the apply confirmation's counts) are built from.
    /// </summary>
    public static RestoreRowCounts Summarize(IReadOnlyList<RestoreFileRow> rows) => new(
        New: rows.Count(r => r.Entry.Status == RestoreFileStatus.New),
        Changed: rows.Count(r => r.Entry.Status == RestoreFileStatus.Changed),
        Identical: rows.Count(r => r.Entry.Status == RestoreFileStatus.Identical),
        LiveOnly: rows.Count(r => r.Entry.Status == RestoreFileStatus.LiveOnly));
}

/// <summary>Per-status row counts for a classified preview (see <see cref="RestoreDisplayModel.Summarize"/>).</summary>
public sealed record RestoreRowCounts(int New, int Changed, int Identical, int LiveOnly)
{
    public int Total => New + Changed + Identical + LiveOnly;
}

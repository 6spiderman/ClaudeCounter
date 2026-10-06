namespace ClaudeBackup;

/// <summary>
/// Pure selection logic for Drive backup retention (design spec: Part 2,
/// "Drive retention"). Takes a listing of what is actually at the
/// destination plus the two independently-switchable settings and returns
/// the names to delete - no rclone invocation, no filesystem I/O, fully
/// unit-testable. Two callers, one per transport (S14): RcloneBackend turns
/// <see cref="RemoteFile"/> from a parsed `rclone lsjson` listing and issues
/// one `rclone deletefile` per name returned; SyncFolderBackend turns it from
/// a plain <c>Directory.EnumerateFiles</c> listing and issues one
/// <c>File.Delete</c> per name returned. <see cref="RestoreZipSource"/>'s
/// sync-folder listing reuses <see cref="IsOurs"/> the same way both
/// backends' retention logic does, to filter a directory listing down to
/// restorable snapshots.
///
/// Lives in ClaudeCounter.Core (not ClaudeBackup.csproj, where it was
/// originally defined) so the restore engine - itself in Core, for the
/// reasons documented on <see cref="IProcessRunner"/> - can call <see
/// cref="IsOurs"/> directly instead of duplicating the naming-scheme check a
/// third time. The namespace stays "ClaudeBackup" so RcloneBackend and
/// SyncFolderBackend (both in ClaudeBackup.csproj) keep compiling unchanged;
/// only the physical file moved.
///
/// Three non-negotiable safety rules, all enforced here (not by the caller):
/// 1. Only <c>claude-backup-*.zip</c> entries are ever candidates - anything
///    else at the destination is invisible to this method, never selected.
/// 2. When both settings are set, a zip is doomed if EITHER rule would
///    remove it (the union) - the user's explicit choice per the spec.
/// 3. The single newest matching entry is NEVER selected, however
///    aggressive the settings are - a pair of settings like KeepLastCount=0
///    and DeleteOlderThanDays=0 must not be able to empty the destination.
/// </summary>
public static class DriveRetention
{
    private const string FilePrefix = "claude-backup-";
    private const string FileSuffix = ".zip";

    /// <summary>One entry from an `rclone lsjson` listing, or a plain directory listing - a candidate backup file.</summary>
    public sealed record RemoteFile(string Name, DateTimeOffset Modified);

    /// <summary>
    /// True when <paramref name="name"/> is one of ours - the only files
    /// this class (and therefore Drive retention pruning, for either
    /// transport) will ever consider deleting. Matches the naming scheme the
    /// shared archive builder (ClaudeBackup.csproj) writes for both
    /// RcloneBackend and SyncFolderBackend: "claude-backup-" + ... + ".zip".
    /// </summary>
    public static bool IsOurs(string name) =>
        name.StartsWith(FilePrefix, StringComparison.Ordinal) &&
        name.EndsWith(FileSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Returns the names (from <paramref name="files"/>) to delete, applying
    /// <paramref name="keepLastCount"/> and <paramref name="deleteOlderThanDays"/>
    /// as a union (either rule dooms an entry) and never dooming the single
    /// newest matching entry - see this class's remarks for the three rules.
    /// Both settings null (nothing configured) returns an empty list without
    /// even looking at the listing's contents beyond the ownership filter.
    /// </summary>
    public static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<RemoteFile> files, int? keepLastCount, int? deleteOlderThanDays)
    {
        var candidates = files
            .Where(f => IsOurs(f.Name))
            .OrderByDescending(f => f.Modified)
            .ThenBy(f => f.Name, StringComparer.Ordinal) // stable tie-break for equal timestamps
            .ToList();

        // Nothing to safely prune once - deleting the only (or last
        // remaining) entry is exactly what rule 3 forbids, so there is no
        // point even evaluating the rules below against a single candidate.
        if (candidates.Count <= 1 || (keepLastCount is null && deleteOlderThanDays is null))
            return Array.Empty<string>();

        var doomed = new HashSet<string>(StringComparer.Ordinal);

        if (keepLastCount is { } keep)
        {
            // Skip(negative) throws nowhere in LINQ - a negative "keep" is
            // clamped to 0 (delete everything the age rule does not save,
            // subject to the last-remaining floor below) rather than being
            // treated as "keep everything".
            foreach (var f in candidates.Skip(Math.Max(0, keep)))
                doomed.Add(f.Name);
        }

        if (deleteOlderThanDays is { } days)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(Math.Max(0, days));
            foreach (var f in candidates.Where(f => f.Modified < cutoff))
                doomed.Add(f.Name);
        }

        // Rule 3: never delete the last remaining backup. candidates is
        // sorted newest-first, so candidates[0] is the one to spare when the
        // rules above would otherwise doom every matching entry.
        if (doomed.Count >= candidates.Count)
            doomed.Remove(candidates[0].Name);

        return candidates.Where(f => doomed.Contains(f.Name)).Select(f => f.Name).ToList();
    }
}

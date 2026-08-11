namespace ClaudeBackup;

/// <summary>
/// Pure selection logic for Drive backup retention (design spec: Part 2,
/// "Drive retention"). Takes a listing of what is actually on the remote
/// plus the two independently-switchable settings and returns the names to
/// delete - no rclone invocation, no I/O, fully unit-testable. RcloneBackend
/// is the only caller: it turns <see cref="RemoteFile"/> from a parsed
/// `rclone lsjson` listing, calls <see cref="SelectForDeletion"/>, and issues
/// one `rclone deletefile` per name returned.
///
/// Three non-negotiable safety rules, all enforced here (not by the caller):
/// 1. Only <c>claude-backup-*.zip</c> entries are ever candidates - anything
///    else in the remote path is invisible to this method, never selected.
/// 2. When both settings are set, a zip is doomed if EITHER rule would
///    remove it (the union) - the user's explicit choice per the spec.
/// 3. The single newest matching entry is NEVER selected, however
///    aggressive the settings are - a pair of settings like KeepLastCount=0
///    and DeleteOlderThanDays=0 must not be able to empty the remote.
/// </summary>
public static class DriveRetention
{
    private const string FilePrefix = "claude-backup-";
    private const string FileSuffix = ".zip";

    /// <summary>One entry from an `rclone lsjson` listing - a candidate backup file.</summary>
    public sealed record RemoteFile(string Name, DateTimeOffset Modified);

    /// <summary>
    /// True when <paramref name="name"/> is one of ours - the only files
    /// this class (and therefore Drive retention pruning) will ever
    /// consider deleting. Matches the prefix RcloneBackend.Run actually
    /// writes (see its zipPath construction): "claude-backup-" + ... + ".zip".
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

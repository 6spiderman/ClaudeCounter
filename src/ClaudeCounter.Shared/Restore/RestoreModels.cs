namespace ClaudeBackup;

/// <summary>
/// One available backup snapshot from a restore source. <see cref="Id"/> is
/// whatever <see cref="RestoreZipSource.Materialize"/> / <see
/// cref="RestoreGitSource.Materialize"/> need to fetch it again: the exact
/// zip file name for Drive, the full commit SHA for GitHub. <see
/// cref="SizeBytes"/> is only ever populated for Drive - git does not give a
/// cheap per-commit size, so it is always null for a GitHub snapshot.
/// </summary>
public sealed record RestoreSnapshot(
    string Id,
    DateTimeOffset Timestamp,
    string DisplayName,
    long? SizeBytes);

/// <summary>Result of listing available snapshots from one destination (design spec step 2).</summary>
public sealed record RestoreListResult(bool Ok, string Message, IReadOnlyList<RestoreSnapshot> Snapshots)
{
    public static RestoreListResult Success(IReadOnlyList<RestoreSnapshot> snapshots) =>
        new(true, "", snapshots);

    public static RestoreListResult Failure(string message) =>
        new(false, message, Array.Empty<RestoreSnapshot>());
}

/// <summary>
/// Result of materialising a chosen snapshot into a staging folder (design
/// spec step 3). <see cref="StagedRoot"/> is the folder <see
/// cref="RestoreClassifier.Classify"/> should be pointed at - it is null
/// whenever <see cref="Ok"/> is false.
/// </summary>
public sealed record RestoreMaterializeResult(bool Ok, string Message, string? StagedRoot)
{
    public static RestoreMaterializeResult Success(string stagedRoot) => new(true, "", stagedRoot);
    public static RestoreMaterializeResult Failure(string message) => new(false, message, null);
}

/// <summary>How one file compares between the staged snapshot and the live tree (design spec step 4).</summary>
public enum RestoreFileStatus
{
    /// <summary>Absent live - applying this entry creates a new file.</summary>
    New,

    /// <summary>Present live with different content - applying this entry overwrites it.</summary>
    Changed,

    /// <summary>Present in both, byte-identical - nothing to do.</summary>
    Identical,

    /// <summary>
    /// Present live, absent from the backup - restore rule 3 ("restore never
    /// deletes"): reported for information, and never in the set <see
    /// cref="RestoreApplier.Apply"/> will actually write.
    /// </summary>
    LiveOnly,
}

/// <summary>
/// One classified file. Sizes/timestamps are populated on whichever side the
/// file actually exists - both sides for <see cref="RestoreFileStatus.Changed"/>
/// and <see cref="RestoreFileStatus.Identical"/>, staged-only for <see
/// cref="RestoreFileStatus.New"/>, live-only for <see cref="RestoreFileStatus.LiveOnly"/>.
///
/// <b>Caveat (fix round 1, Minor):</b> <see cref="StagedModifiedUtc"/> for a
/// GitHub-sourced snapshot is NOT the commit's own timestamp - it is
/// whatever <see cref="File.LastWriteTimeUtc(string)"/> reads back after
/// RestoreGitSource.CopySafely writes the file out of the git worktree, i.e.
/// close to "now", for every file in the snapshot alike. A Drive-sourced
/// snapshot's <see cref="StagedModifiedUtc"/> is the zip entry's own stored
/// modification time and does not have this problem. A caller presenting
/// this as a "last modified" column must not treat the GitHub case as
/// meaningful per-file information - <see cref="RestoreSnapshot.Timestamp"/>
/// (the commit's own timestamp, one value for the whole snapshot) is the
/// trustworthy signal there instead.
/// </summary>
public sealed record RestoreFileEntry(
    string RelativePath,
    RestoreFileStatus Status,
    long? StagedSizeBytes,
    DateTimeOffset? StagedModifiedUtc,
    long? LiveSizeBytes,
    DateTimeOffset? LiveModifiedUtc);

/// <summary>
/// Result of applying a chosen subset of classified entries to live
/// ~/.claude (design spec step 6). <see cref="SafetyCopyPath"/> is the exact
/// path rule 2 requires be logged and shown - null only when nothing was
/// actually overwritten (no safety copy was needed, so no safety folder was
/// created). <see cref="SkippedPaths"/> lists every requested entry that was
/// NOT applied and why (LiveOnly/Identical, a denylist hit, an unsafe path,
/// or a staged file that went missing) - each reason is also logged
/// individually via Log.Warn as it happens.
/// </summary>
public sealed record RestoreApplyResult(
    bool Ok,
    string Message,
    string? SafetyCopyPath,
    int CreatedCount,
    int OverwrittenCount,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> SkippedPaths);

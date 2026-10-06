using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Restore source for the Drive destination (design spec step 2/3, Drive
/// half), for EITHER transport (S14: rclone or sync-folder - see the design
/// doc's "Key structural decision", a transport switch on this one
/// destination, not a third one). <see cref="ListSnapshots"/> and <see
/// cref="Materialize"/> both branch internally on <see
/// cref="DriveTarget.Transport"/> - their signatures take the whole <see
/// cref="DriveTarget"/> already, so the branch is invisible to every caller,
/// including <c>RestoreDialog</c>, which needed no changes at all for this.
///
/// Rclone transport: lists <c>claude-backup-*.zip</c> entries via <c>rclone
/// lsjson</c>, and materialises a chosen zip into a staging folder via
/// <c>rclone copy</c> + extraction. All rclone invocations go through <see
/// cref="IProcessRunner"/>, exactly like <see cref="RcloneBackend"/>, so this
/// is fully unit-testable without a real rclone binary or a real remote.
///
/// Sync-folder transport: lists <c>claude-backup-*.zip</c> entries directly
/// out of <see cref="DriveTarget.FolderPath"/> via
/// <see cref="Directory.EnumerateFiles(string, string)"/> (filtered through
/// <see cref="DriveRetention.IsOurs"/>, the same ownership check
/// SyncFolderBackend's own retention pruning uses), and materialises a
/// chosen snapshot by opening it directly in place - there is no download
/// step, and critically nothing is ever deleted from FolderPath here: unlike
/// the rclone transport's downloaded temp copy, the zip IS the backup, not a
/// disposable local copy of it.
///
/// Extraction (<see cref="ExtractSafely"/>) enforces restore rule 5
/// (containment: a zip-slip entry such as "../evil.json" is refused, logged,
/// and never written outside the destination) and restore rule 4 (denylist
/// backstop: an entry named ".credentials.json" is refused) on every single
/// zip entry before anything is written - this is the "on the way IN" check
/// the design spec calls for, so a tampered archive cannot drop a secret or
/// an out-of-tree file into the destination even before <see
/// cref="RestoreClassifier"/> or <see cref="RestoreApplier"/> ever run. This,
/// like the destination-overlap refusal and <see cref="IsPlausibleSnapshotId"/>,
/// is shared by both transports untouched.
/// </summary>
public sealed class RestoreZipSource
{
    private readonly IProcessRunner _runner;

    public RestoreZipSource(IProcessRunner runner) => _runner = runner;

    private const string FilePrefix = "claude-backup-";
    private const string FileSuffix = ".zip";

    /// <summary>
    /// Lists available Drive snapshots, newest first, for whichever
    /// transport <paramref name="target"/> is configured for. Filters to
    /// <c>claude-backup-*.zip</c> - anything else living alongside it (a
    /// user's own file, a different tool's output) is silently ignored
    /// rather than offered as a restorable snapshot.
    /// </summary>
    public RestoreListResult ListSnapshots(DriveTarget target) =>
        target.Transport == DriveTransport.SyncFolder
            ? ListSnapshotsFromFolder(target)
            : ListSnapshotsFromRclone(target);

    private RestoreListResult ListSnapshotsFromRclone(DriveTarget target)
    {
        if (!_runner.Exists("rclone"))
            return RestoreListResult.Failure(ScrubAndLog("rclone not found on PATH - install and run 'rclone config' first."));

        ProcessResult listing;
        try
        {
            listing = _runner.Run("rclone", new[] { "lsjson", target.RcloneRemote });
        }
        catch (Exception ex)
        {
            return RestoreListResult.Failure(ScrubAndLog($"rclone lsjson threw: {ex.Message}"));
        }

        if (!listing.Ok)
            return RestoreListResult.Failure(ScrubAndLog($"rclone lsjson failed: {listing.StdErr.Trim()}"));

        List<RestoreSnapshot> snapshots;
        try
        {
            snapshots = ParseLsJson(listing.StdOut);
        }
        catch (JsonException ex)
        {
            return RestoreListResult.Failure(ScrubAndLog($"rclone lsjson returned output that could not be parsed: {ex.Message}"));
        }

        return RestoreListResult.Success(snapshots.OrderByDescending(s => s.Timestamp).ToList());
    }

    /// <summary>
    /// S14: the sync-folder equivalent of <see cref="ListSnapshotsFromRclone"/> -
    /// no process invocation, just a directory listing. A FolderPath that
    /// does not exist yet (the common case for a destination that has never
    /// completed a single backup) is treated the same as "no backups found",
    /// not as a failure - SyncFolderBackend.Run is what creates this
    /// directory, so its absence here just means nothing has landed yet.
    /// </summary>
    private static RestoreListResult ListSnapshotsFromFolder(DriveTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.FolderPath))
            return RestoreListResult.Failure("Sync folder backup is enabled but no folder is configured.");

        if (!Directory.Exists(target.FolderPath))
            return RestoreListResult.Success(Array.Empty<RestoreSnapshot>());

        try
        {
            var snapshots = Directory.EnumerateFiles(target.FolderPath, "claude-backup-*.zip")
                .Select(Path.GetFileName)
                .Where(name => name is not null && DriveRetention.IsOurs(name))
                .Select(name =>
                {
                    var path = Path.Combine(target.FolderPath, name!);
                    var info = new FileInfo(path);
                    return new RestoreSnapshot(
                        name!, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), name!, info.Length);
                })
                .OrderByDescending(s => s.Timestamp)
                .ToList();

            return RestoreListResult.Success(snapshots);
        }
        catch (Exception ex)
        {
            return RestoreListResult.Failure(ScrubAndLog($"Could not list '{target.FolderPath}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Materialises <paramref name="snapshotId"/> (the exact zip file name,
    /// as returned by <see cref="ListSnapshots"/>) into <paramref
    /// name="destinationDir"/> with the containment and denylist checks
    /// described on this class, for whichever transport <paramref
    /// name="target"/> is configured for - see <see cref="MaterializeFromRclone"/>
    /// and <see cref="MaterializeFromFolder"/> for what differs between them.
    ///
    /// <paramref name="destinationDir"/> is CLEARED before extraction (fix
    /// round 1, Important 1) - restoring snapshot A into it and then, later,
    /// snapshot B must never leave A's files behind to be classified as part
    /// of B's staged tree (a union, not the chosen snapshot). This is safe
    /// specifically because a materialisation destination is restore-owned
    /// scratch, never live config - restore rule 3 ("never delete") governs
    /// what happens to <c>liveRoot</c> in RestoreApplier, not this directory.
    /// <paramref name="protectedRoot"/> (fix round 1, Important 3) is the
    /// live config root (in production, the configured BackupConfig.SourceRoot)
    /// that <paramref name="destinationDir"/> must never be, contain, or be
    /// contained by - refused up front, before the clear above or any write,
    /// so a caller mistake here cannot delete or overwrite live config with
    /// no safety copy and no preview. This check, and the snapshot-id
    /// plausibility check right above it, apply identically to both
    /// transports and so are done once here rather than once per transport.
    /// </summary>
    public RestoreMaterializeResult Materialize(
        DriveTarget target, string snapshotId, string tempDir, string destinationDir, string protectedRoot)
    {
        if (!IsPlausibleSnapshotId(snapshotId))
            return RestoreMaterializeResult.Failure("Invalid snapshot id.");

        if (RelativePathGuard.Overlaps(destinationDir, protectedRoot))
        {
            return RestoreMaterializeResult.Failure(
                "Refusing to materialise into the live config root, or a directory that contains or is contained by it.");
        }

        return target.Transport == DriveTransport.SyncFolder
            ? MaterializeFromFolder(target, snapshotId, destinationDir)
            : MaterializeFromRclone(target, snapshotId, tempDir, destinationDir);
    }

    /// <summary>
    /// Downloads <paramref name="snapshotId"/> into <paramref
    /// name="tempDir"/> via <c>rclone copy</c>, then extracts it into
    /// <paramref name="destinationDir"/>. The downloaded zip is deleted from
    /// <paramref name="tempDir"/> afterwards on a best-effort basis (mirrors
    /// RcloneBackend's own temp-zip cleanup) - a leftover zip would be a
    /// second plaintext copy of the user's Claude config sitting on disk.
    /// </summary>
    private RestoreMaterializeResult MaterializeFromRclone(
        DriveTarget target, string snapshotId, string tempDir, string destinationDir)
    {
        if (!_runner.Exists("rclone"))
            return RestoreMaterializeResult.Failure(ScrubAndLog("rclone not found on PATH - install and run 'rclone config' first."));

        try
        {
            Directory.CreateDirectory(tempDir);

            var remotePath = CombineRemotePath(target.RcloneRemote, snapshotId);
            var copy = _runner.Run("rclone", new[] { "copy", remotePath, tempDir });
            if (!copy.Ok)
                return RestoreMaterializeResult.Failure(ScrubAndLog($"rclone copy failed: {copy.StdErr.Trim()}"));

            var zipPath = Path.Combine(tempDir, snapshotId);
            if (!File.Exists(zipPath))
                return RestoreMaterializeResult.Failure("rclone copy reported success but the downloaded zip was not found locally.");

            Directory.CreateDirectory(destinationDir);
            ClearDirectoryContents(destinationDir);
            ExtractSafely(zipPath, destinationDir);

            return RestoreMaterializeResult.Success(destinationDir);
        }
        catch (InvalidDataException ex)
        {
            // ZipFile throws this for a corrupt or non-zip file - fail
            // cleanly rather than letting it propagate as an unhandled
            // exception out of a restore-preview code path.
            return RestoreMaterializeResult.Failure($"Downloaded backup is not a valid zip archive: {ex.Message}");
        }
        catch (Exception ex)
        {
            return RestoreMaterializeResult.Failure(ScrubAndLog(ex.Message));
        }
        finally
        {
            TryDeleteDownloadedZip(tempDir, snapshotId);
        }
    }

    /// <summary>
    /// S14 sync-folder transport: no download step at all - the archive
    /// already lives at <see cref="DriveTarget.FolderPath"/> (a local path,
    /// or a mapped/UNC path a sync client or NAS presents as one), so this
    /// opens it directly in place. Nothing under FolderPath is ever deleted
    /// here - unlike the rclone transport's downloaded temp copy, this zip
    /// IS the backup, not a disposable local copy of it.
    /// </summary>
    private static RestoreMaterializeResult MaterializeFromFolder(
        DriveTarget target, string snapshotId, string destinationDir)
    {
        if (string.IsNullOrWhiteSpace(target.FolderPath))
            return RestoreMaterializeResult.Failure("Sync folder backup is enabled but no folder is configured.");

        var zipPath = Path.Combine(target.FolderPath, snapshotId);
        if (!File.Exists(zipPath))
            return RestoreMaterializeResult.Failure($"Backup '{snapshotId}' was not found in '{target.FolderPath}'.");

        try
        {
            Directory.CreateDirectory(destinationDir);
            ClearDirectoryContents(destinationDir);
            ExtractSafely(zipPath, destinationDir);

            return RestoreMaterializeResult.Success(destinationDir);
        }
        catch (InvalidDataException ex)
        {
            return RestoreMaterializeResult.Failure($"Backup is not a valid zip archive: {ex.Message}");
        }
        catch (Exception ex)
        {
            return RestoreMaterializeResult.Failure(ScrubAndLog(ex.Message));
        }
    }

    /// <summary>
    /// Extracts every entry in <paramref name="zipPath"/> into <paramref
    /// name="destinationDir"/>, refusing (and logging) any entry that fails
    /// either restore rule 5 (containment) or restore rule 4 (the secret
    /// denylist) instead of extracting it. A refused entry does not abort
    /// extraction of the rest of the archive - one bad or malicious entry
    /// must not prevent the legitimate ones from being materialised for
    /// preview.
    /// </summary>
    private static void ExtractSafely(string zipPath, string destinationDir)
    {
        var destRootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDir));

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            // A directory entry has an empty Name (only FullName carries the
            // trailing "/") - nothing to extract, and no path to validate.
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var rel = entry.FullName;

            if (!RelativePathGuard.IsSafe(rel))
            {
                Log.Warn($"RestoreZipSource: refusing unsafe zip entry path (possible zip-slip): '{rel}'.");
                continue;
            }

            if (SecretDenylist.IsSecret(rel))
            {
                Log.Warn($"RestoreZipSource: refusing to extract denylisted file: '{rel}'.");
                continue;
            }

            var destPath = Path.Combine(destinationDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var destFull = Path.GetFullPath(destPath);

            // Belt-and-braces alongside RelativePathGuard.IsSafe, mirroring
            // GitBackend.MirrorFiles: resolve the actual destination and
            // verify it is still under destinationDir before writing. Written
            // out inline rather than through RelativePathGuard.IsWithinDirectory
            // (same check): CodeQL's zip-slip query only recognises a
            // StartsWith containment check in the method that does the
            // extraction, and flagged this one as unsanitised otherwise.
            // Ordinal is exact here - both sides come from the same
            // GetFullPath normalisation of destinationDir.
            if (!destFull.StartsWith(destRootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Log.Warn($"RestoreZipSource: refusing zip entry that resolves outside the destination: '{rel}'.");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            entry.ExtractToFile(destFull, overwrite: true);
        }
    }

    /// <summary>
    /// Deletes every entry directly under <paramref name="dir"/> (recursively
    /// for subdirectories) so a fresh Materialize call never leaves a
    /// previous snapshot's files behind for RestoreClassifier to see as part
    /// of the new one - fix round 1, Important 1. Assumes <paramref
    /// name="dir"/> already exists (callers create it first) and is
    /// restore-owned scratch, not live config - restore rule 3 does not
    /// apply here.
    /// </summary>
    private static void ClearDirectoryContents(string dir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
    }

    private static void TryDeleteDownloadedZip(string tempDir, string snapshotId)
    {
        if (!IsPlausibleSnapshotId(snapshotId))
            return;

        var path = Path.Combine(tempDir, snapshotId);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"RestoreZipSource: failed to delete downloaded temp zip '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// snapshotId reaches this class as an "arbitrary string a caller passed
    /// in" as far as the type system is concerned (it usually originated from
    /// <see cref="ListSnapshots"/>'s own output, but Materialize is public
    /// API and must not assume that). Rejecting anything that is not a safe,
    /// well-formed "claude-backup-*.zip" name before it is ever combined into
    /// a remote path or a local file path closes off both a path-traversal
    /// vector (a snapshotId containing "../") and an rclone argument-
    /// injection vector (one starting with "-").
    /// </summary>
    private static bool IsPlausibleSnapshotId(string snapshotId) =>
        !string.IsNullOrWhiteSpace(snapshotId)
        && RelativePathGuard.IsSafe(snapshotId)
        && !snapshotId.StartsWith('-')
        && snapshotId.StartsWith(FilePrefix, StringComparison.Ordinal)
        && snapshotId.EndsWith(FileSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Joins an rclone remote spec (e.g. "gdrive:ClaudeBackups") with a bare
    /// file name for an `rclone copy` source - rclone remotes are
    /// colon-separated, not path-separated, so Path.Combine (which knows
    /// nothing about that syntax) is not used here. Mirrors
    /// RcloneBackend.CombineRemotePath.
    /// </summary>
    private static string CombineRemotePath(string remote, string fileName) =>
        remote.EndsWith('/') || remote.EndsWith(':') ? remote + fileName : remote + "/" + fileName;

    /// <summary>
    /// Parses `rclone lsjson`'s output into <see cref="RestoreSnapshot"/>,
    /// filtered to `claude-backup-*.zip` and tolerant of anything unexpected
    /// (a directory entry, a missing Name/ModTime, an unparsable timestamp)
    /// by skipping just that one entry - mirrors RcloneBackend.ParseLsJson.
    /// </summary>
    private static List<RestoreSnapshot> ParseLsJson(string json)
    {
        var result = new List<RestoreSnapshot>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("IsDir", out var isDir) && isDir.ValueKind == JsonValueKind.True)
                continue;

            var name = entry.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrEmpty(name))
                continue;
            if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(FileSuffix, StringComparison.Ordinal))
                continue; // not one of ours - ignored, not offered as a restorable snapshot

            var modText = entry.TryGetProperty("ModTime", out var modProp) ? modProp.GetString() : null;
            if (string.IsNullOrEmpty(modText))
                continue;
            if (!DateTimeOffset.TryParse(
                    modText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var modified))
                continue;

            long? size = entry.TryGetProperty("Size", out var sizeProp) && sizeProp.TryGetInt64(out var s) ? s : null;

            result.Add(new RestoreSnapshot(name, modified, name, size));
        }
        return result;
    }

    private static string ScrubAndLog(string message)
    {
        var scrubbed = CredentialScrubber.Scrub(message);
        Log.Warn($"RestoreZipSource: {scrubbed}");
        return scrubbed;
    }
}

using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// S14 design doc ("sync-folder backup transport"): snapshots a selection of
/// files into a zip archive - via the shared <see cref="BackupArchiveWriter"/>,
/// exactly the same builder <see cref="RcloneBackend"/> uses - and copies it
/// straight into <see cref="DriveTarget.FolderPath"/>: a plain folder a sync
/// client (Google Drive for Desktop, OneDrive, Dropbox) or a NAS share
/// already watches and uploads/replicates on its own. That is the entire
/// point of this transport: no external binary, no OAuth walkthrough, no
/// authentication of any kind on this app's side - the sync client (or the
/// NAS itself) does the actual upload. Accordingly this class invokes no
/// external process and takes no <see cref="IProcessRunner"/> at all, unlike
/// <see cref="GitBackend"/> and <see cref="RcloneBackend"/>.
///
/// Folder-path validation happens here, at the point of write (mirroring
/// where RcloneBackend checks "rclone not found on PATH" - a precondition
/// checked before anything is written, not earlier in the pipeline):
/// <list type="bullet">
/// <item>The path must be non-blank and rooted. A UNC path such as
/// "\\nas\share\claude" is explicitly ALLOWED - <see cref="Path.IsPathRooted(string)"/>
/// already returns true for one, and a NAS share is a first-class target for
/// this transport, not an edge case.</item>
/// <item>The path must not overlap <paramref name="sourceRoot"/> (checked via
/// <see cref="RelativePathGuard.Overlaps"/>) - THE single most important
/// guard here. Without it the archive would be written inside the tree being
/// backed up, and every subsequent run's file selection would sweep up its
/// own predecessors' zips, growing without bound and backing up backups of
/// backups.</item>
/// <item>The destination directory is created if it does not already exist;
/// a failure to create it fails the run with a clear message rather than
/// throwing out of Run.</item>
/// </list>
/// The leading-dash and embedded-credential checks <c>SettingsForm</c> applies
/// to <see cref="DriveTarget.RcloneRemote"/> do NOT apply here and are
/// deliberately not reproduced - <see cref="DriveTarget.FolderPath"/> never
/// reaches a process argument list or a URL, so neither hazard exists for it.
///
/// Retention reuses <see cref="DriveRetention.SelectForDeletion"/> verbatim -
/// the exact same pure rule RcloneBackend uses - built from
/// <see cref="Directory.EnumerateFiles(string, string)"/> plus
/// <see cref="File.GetLastWriteTimeUtc(string)"/> instead of a parsed
/// `rclone lsjson` listing. Pruning runs only after a successful copy, inside
/// its own try/catch (mirroring RcloneBackend's own ordering - design spec,
/// Part 2, retention rule 1): a pruning failure can never downgrade a
/// successful backup into a failed <see cref="BackendResult"/>.
/// </summary>
public sealed class SyncFolderBackend
{
    private readonly string _tempDir;

    public SyncFolderBackend(string tempDir)
    {
        _tempDir = tempDir;
    }

    public BackendResult Run(string sourceRoot, IReadOnlyList<string> files, DriveTarget target)
    {
        if (ValidateFolderPath(target.FolderPath, sourceRoot) is { } validationError)
            return Fail(validationError);

        try
        {
            Directory.CreateDirectory(target.FolderPath);
        }
        catch (Exception ex)
        {
            return Fail($"Could not create sync folder '{target.FolderPath}': {ex.Message}");
        }

        var zipPath = BackupArchiveWriter.BuildZipPath(_tempDir);
        try
        {
            Directory.CreateDirectory(_tempDir);
            BackupArchiveWriter.SweepStaleZips(_tempDir, "SyncFolderBackend");

            BackupArchiveWriter.CreateArchive(zipPath, sourceRoot, files, "SyncFolderBackend");

            var destPath = Path.Combine(target.FolderPath, Path.GetFileName(zipPath)!);
            try
            {
                File.Copy(zipPath, destPath, overwrite: false);
            }
            catch (Exception ex)
            {
                return Fail($"Could not copy backup into '{target.FolderPath}': {ex.Message}");
            }

            Log.Info($"SyncFolderBackend: copy OK to '{target.FolderPath}'.");

            // Retention rule 1 (design spec, Part 2), same as RcloneBackend:
            // pruning runs ONLY after the copy above already succeeded, and
            // any failure here - including one PruneOldBackups itself did
            // not already catch - is logged and swallowed, never turning a
            // successful copy into a failed BackendResult.
            try
            {
                PruneOldBackups(target);
            }
            catch (Exception ex)
            {
                Log.Warn($"SyncFolderBackend: retention pruning threw and was skipped: {ex.Message}");
            }

            return new BackendResult(true, "Sync folder backup complete.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            // Unconditional, exactly like RcloneBackend.Run's own finally -
            // this runs whether Run is about to return success, a failure,
            // or propagate an exception.
            BackupArchiveWriter.DeleteZip(zipPath, "SyncFolderBackend");
        }
    }

    /// <summary>
    /// The three folder-path preconditions (see this class's own doc
    /// comment): non-blank, rooted (a UNC path such as "\\nas\share\claude"
    /// is explicitly valid - a NAS share is a first-class target for this
    /// transport), and not overlapping <paramref name="sourceRoot"/> (design
    /// doc: THE single most important guard here - without it the archive
    /// would be written inside the tree being backed up, and every
    /// subsequent run would back up its own previous backups). Returns null
    /// when valid, or the exact message <see cref="Run"/> should fail with.
    /// Side-effect-free (no filesystem I/O beyond path-string normalisation,
    /// no network access even for a UNC path) so it is directly unit-testable
    /// without a real NAS share or a real directory on disk. Internal (not
    /// private) so it can be tested directly - mirrors GitBackend.IsWithinDirectory.
    /// </summary>
    internal static string? ValidateFolderPath(string folderPath, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return "Sync folder backup is enabled but no folder is configured.";

        if (!Path.IsPathRooted(folderPath))
            return $"Sync folder path '{folderPath}' is not a full path - enter a full local or UNC path.";

        if (RelativePathGuard.Overlaps(folderPath, sourceRoot))
        {
            return $"Sync folder path '{folderPath}' overlaps the backup source root '{sourceRoot}' - " +
                   "choose a folder outside the tree being backed up.";
        }

        return null;
    }

    /// <summary>
    /// Drive retention (design spec, Part 2) for the sync-folder transport:
    /// enumerates <see cref="DriveTarget.FolderPath"/> directly instead of
    /// parsing an `rclone lsjson` listing, applies
    /// <see cref="DriveRetention.SelectForDeletion"/> - the identical pure
    /// rule RcloneBackend uses - and deletes each doomed entry via
    /// <see cref="File.Delete(string)"/>. A no-op when neither
    /// <see cref="DriveTarget.KeepLastCount"/> nor
    /// <see cref="DriveTarget.DeleteOlderThanDays"/> is configured.
    /// </summary>
    private static void PruneOldBackups(DriveTarget target)
    {
        if (target.KeepLastCount is null && target.DeleteOlderThanDays is null)
            return;

        List<DriveRetention.RemoteFile> files;
        try
        {
            files = Directory.EnumerateFiles(target.FolderPath, "claude-backup-*.zip")
                .Select(path => new DriveRetention.RemoteFile(Path.GetFileName(path)!, File.GetLastWriteTimeUtc(path)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"SyncFolderBackend: failed to list '{target.FolderPath}' for retention pruning: {ex.Message}");
            return;
        }

        var doomed = DriveRetention.SelectForDeletion(files, target.KeepLastCount, target.DeleteOlderThanDays);
        foreach (var name in doomed)
        {
            var path = Path.Combine(target.FolderPath, name);
            try
            {
                File.Delete(path);
                Log.Info($"SyncFolderBackend: retention pruning deleted '{name}' from '{target.FolderPath}'.");
            }
            catch (Exception ex)
            {
                Log.Warn($"SyncFolderBackend: retention pruning failed to delete '{name}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Single choke point for anything that becomes a <see cref="BackendResult.Message"/>
    /// or a log line. No credential scrubbing (unlike GitBackend.Fail /
    /// RcloneBackend.Fail): a folder path never carries a credential the way
    /// a remote URL can, so there is nothing here for CredentialScrubber to do.
    /// </summary>
    private static BackendResult Fail(string message)
    {
        Log.Warn($"SyncFolderBackend: {message}");
        return new BackendResult(false, message);
    }
}

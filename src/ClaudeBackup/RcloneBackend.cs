using System.Globalization;
using System.Text.Json;
using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Snapshots a selection of files into a zip archive and copies it to a
/// remote configured for rclone (e.g. Google Drive). All rclone invocations
/// go through <see cref="IProcessRunner"/> so this is fully unit-testable
/// without a real rclone binary or a real remote.
///
/// The zip is written under <c>tempDir</c> (the caller is expected to point
/// this at a private location under %LOCALAPPDATA%\ClaudeCounter\), via the
/// shared <see cref="BackupArchiveWriter"/> (S14: extracted so <see
/// cref="SyncFolderBackend"/> - the sync-folder transport added alongside
/// this one - shares the exact same zip-building and temp-zip-hygiene code
/// rather than a second copy of it; see that class's own doc comment). <see
/// cref="Run"/> always ATTEMPTS to delete the zip before returning - on
/// success, on an upload failure, or on an exception thrown while zipping -
/// because a leftover zip is a plaintext copy of the user's Claude config
/// sitting on disk. That delete can itself fail (an AV scanner or an indexer
/// holding a handle on a just-written file is a real, not hypothetical,
/// failure mode) - when it does, the failure is logged loudly rather than
/// swallowed. The next call to Run also sweeps any zip left behind by such a
/// failure, but only ones old enough and unlocked enough to be confidently
/// NOT a concurrently running instance's own in-flight archive - see
/// <see cref="BackupArchiveWriter.SweepStaleZips"/>.
/// Every string that can reach a log line, an exception message, or a
/// <see cref="BackendResult.Message"/> is funneled through <see cref="Fail"/>,
/// which scrubs credential-bearing URL fragments via the shared
/// <see cref="CredentialScrubber"/> before the text goes anywhere - this
/// matters because rclone can echo a remote spec (e.g. a WebDAV/S3 URL with
/// an embedded token) back on stderr when a copy fails.
/// </summary>
public sealed class RcloneBackend
{
    private readonly IProcessRunner _runner;
    private readonly string _tempDir;

    public RcloneBackend(IProcessRunner runner, string tempDir)
    {
        _runner = runner;
        _tempDir = tempDir;
    }

    public BackendResult Run(string sourceRoot, IReadOnlyList<string> files, DriveTarget target)
    {
        if (!_runner.Exists("rclone"))
            return Fail("rclone not found on PATH - install and run 'rclone config' first.");

        var zipPath = BackupArchiveWriter.BuildZipPath(_tempDir);
        try
        {
            Directory.CreateDirectory(_tempDir);
            BackupArchiveWriter.SweepStaleZips(_tempDir, "RcloneBackend");

            BackupArchiveWriter.CreateArchive(zipPath, sourceRoot, files, "RcloneBackend");

            var copy = _runner.Run("rclone", new[] { "copy", zipPath, target.RcloneRemote });
            if (!copy.Ok)
                return Fail($"rclone copy failed: {copy.StdErr}".Trim());

            Log.Info("RcloneBackend: copy OK.");

            // Retention rule 1 (design spec, Part 2): pruning runs ONLY
            // after the upload above already succeeded, so a failed run can
            // never shrink the backup set. Wrapped so that ANY unexpected
            // failure here - not just the ones PruneOldBackups itself
            // already catches - cannot turn a successful upload into a
            // failed BackendResult; a pruning failure is logged and
            // swallowed, never propagated.
            try
            {
                PruneOldBackups(target);
            }
            catch (Exception ex)
            {
                Log.Warn($"RcloneBackend: retention pruning threw and was skipped: {ex.Message}");
            }

            return new BackendResult(true, "Google Drive backup complete.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            // Unconditional: this runs whether Run is about to return
            // success, a failure, or propagate an exception. A failed
            // delete is NOT swallowed silently - see BackupArchiveWriter.DeleteZip.
            BackupArchiveWriter.DeleteZip(zipPath, "RcloneBackend");
        }
    }

    /// <summary>
    /// Drive retention (design spec, Part 2): lists the remote, applies
    /// <see cref="DriveRetention.SelectForDeletion"/> (the pure rule logic,
    /// separately unit tested), and deletes each doomed entry. A no-op when
    /// neither <see cref="DriveTarget.KeepLastCount"/> nor <see
    /// cref="DriveTarget.DeleteOlderThanDays"/> is configured - the common
    /// case - so a user who never opened the Advanced dialog pays no extra
    /// rclone call.
    ///
    /// Retention rule from the spec: "If listing fails, log and skip
    /// pruning - a pruning failure must never fail the backup run, which
    /// has already succeeded by then." Every branch below that gives up
    /// (listing failed, listing threw, an individual delete failed) does so
    /// by logging and returning/continuing, never by throwing - this method
    /// itself is additionally wrapped by its caller for defense in depth.
    /// </summary>
    private void PruneOldBackups(DriveTarget target)
    {
        if (target.KeepLastCount is null && target.DeleteOlderThanDays is null)
            return;

        List<DriveRetention.RemoteFile> files;
        try
        {
            var listing = _runner.Run("rclone", new[] { "lsjson", target.RcloneRemote });
            if (!listing.Ok)
            {
                Log.Warn($"RcloneBackend: rclone lsjson failed ({listing.StdErr.Trim()}); skipping retention pruning this run.");
                return;
            }
            files = ParseLsJson(listing.StdOut);
        }
        catch (Exception ex)
        {
            Log.Warn($"RcloneBackend: failed to list '{target.RcloneRemote}' for retention pruning: {ex.Message}");
            return;
        }

        var doomed = DriveRetention.SelectForDeletion(files, target.KeepLastCount, target.DeleteOlderThanDays);
        foreach (var name in doomed)
        {
            var remotePath = CombineRemotePath(target.RcloneRemote, name);
            try
            {
                var delete = _runner.Run("rclone", new[] { "deletefile", remotePath });
                if (delete.Ok)
                    Log.Info($"RcloneBackend: retention pruning deleted '{name}' from Drive.");
                else
                    Log.Warn($"RcloneBackend: retention pruning failed to delete '{name}': {delete.StdErr.Trim()}");
            }
            catch (Exception ex)
            {
                Log.Warn($"RcloneBackend: retention pruning failed to delete '{name}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Parses `rclone lsjson`'s output into the plain (name, modified) pairs
    /// DriveRetention.SelectForDeletion works with. Tolerant of anything
    /// unexpected - a directory entry, a missing Name/ModTime, an
    /// unparsable timestamp - by skipping just that one entry rather than
    /// failing the whole listing, since a partially-odd remote listing is a
    /// better reason to prune conservatively than to abandon pruning
    /// entirely for the run.
    /// </summary>
    private static List<DriveRetention.RemoteFile> ParseLsJson(string json)
    {
        var result = new List<DriveRetention.RemoteFile>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("IsDir", out var isDir) && isDir.ValueKind == JsonValueKind.True)
                continue;

            var name = entry.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
            var modText = entry.TryGetProperty("ModTime", out var modProp) ? modProp.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(modText))
                continue;

            if (!DateTimeOffset.TryParse(
                    modText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var modified))
                continue;

            result.Add(new DriveRetention.RemoteFile(name, modified));
        }
        return result;
    }

    /// <summary>
    /// Joins an rclone remote spec (e.g. "gdrive:ClaudeBackups" or
    /// "gdrive:ClaudeBackups/") with a bare file name for a `deletefile`
    /// call - rclone remotes are colon-separated, not path-separated, so
    /// Path.Combine (which knows nothing about that syntax) is not used here.
    /// </summary>
    private static string CombineRemotePath(string remote, string fileName) =>
        remote.EndsWith('/') || remote.EndsWith(':') ? remote + fileName : remote + "/" + fileName;

    /// <summary>
    /// Single choke point for anything that becomes a <see cref="BackendResult.Message"/>
    /// or a log line: scrubs credentials before logging and before
    /// returning, so no caller of Fail can forget to redact.
    /// </summary>
    private static BackendResult Fail(string message)
    {
        var scrubbed = CredentialScrubber.Scrub(message);
        Log.Warn($"RcloneBackend: {scrubbed}");
        return new BackendResult(false, scrubbed);
    }
}

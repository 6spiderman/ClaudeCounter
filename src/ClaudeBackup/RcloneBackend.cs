using System.Globalization;
using System.IO.Compression;
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
/// this at a private location under %LOCALAPPDATA%\ClaudeCounter\). <see cref="Run"/>
/// always ATTEMPTS to delete it before returning - on success, on an upload
/// failure, or on an exception thrown while zipping - because a leftover
/// zip is a plaintext copy of the user's Claude config sitting on disk. That
/// delete can itself fail (an AV scanner or an indexer holding a handle on a
/// just-written file is a real, not hypothetical, failure mode) - when it
/// does, the failure is logged loudly rather than swallowed. The next call
/// to Run also sweeps any zip left behind by such a failure, but only ones
/// old enough and unlocked enough to be confidently NOT a concurrently
/// running instance's own in-flight archive - see <see cref="SweepStaleZips"/>.
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

        // Unique per call (timestamp + a GUID, not just a timestamp): two
        // runs landing in the same second - e.g. a scheduled run racing a
        // tray "back up now" - must never collide on a zip file name. A
        // collision would make ZipArchiveMode.Create throw for the second
        // run and, worse, would mean the finally block below could delete
        // the FIRST run's zip out from under an in-flight upload.
        var zipPath = Path.Combine(
            _tempDir, $"claude-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        try
        {
            Directory.CreateDirectory(_tempDir);
            SweepStaleZips();

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rel in files)
                {
                    // Defense in depth, matching GitBackend's discipline:
                    // FileSelector is expected to hand back root-relative,
                    // forward-slashed paths with no ".." segment, but this
                    // backend must not assume that.
                    if (!RelativePathGuard.IsSafe(rel))
                    {
                        Log.Warn($"RcloneBackend: refusing unsafe relative path '{rel}'.");
                        continue;
                    }

                    // Fail-closed backstop at the actual point of write,
                    // mirroring GitBackend.MirrorFiles: Run takes an
                    // arbitrary file list as public API, so a secret-named
                    // entry reaching this loop directly must still never be
                    // archived, even though FileSelector and BackupRunner
                    // both already filter upstream.
                    if (SecretDenylist.IsSecret(rel))
                    {
                        Log.Warn($"RcloneBackend: refusing to archive secret-named file (denylist backstop): '{rel}'.");
                        continue;
                    }

                    // Run is public and takes an arbitrary file list - two
                    // distinct source paths that normalize to the same zip
                    // entry name (a caller bug, not something FileSelector's
                    // own sorted, deduplicated output would ever produce)
                    // must not silently overwrite one archive entry with
                    // another.
                    if (!seenEntries.Add(rel))
                    {
                        Log.Warn($"RcloneBackend: refusing duplicate zip entry name: '{rel}'.");
                        continue;
                    }

                    var src = Path.Combine(sourceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(src))
                        zip.CreateEntryFromFile(src, rel);
                    else
                        Log.Warn($"RcloneBackend: source file missing, skipping: '{rel}'.");
                }
            }

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
            // delete is NOT swallowed silently - see DeleteZip.
            DeleteZip(zipPath);
        }
    }

    /// <summary>
    /// Deletes the just-written temp zip. A failure here is the one failure
    /// in this class that most needs to be visible: the file left behind is
    /// a plaintext copy of the user's Claude config, and nothing else will
    /// ever clean it up except the next call's <see cref="SweepStaleZips"/>.
    /// Still does not rethrow - Run's own result (success or a specific
    /// upload failure) must not be replaced by a cleanup failure - but logs
    /// loudly at Error level naming the exact path.
    /// </summary>
    private static void DeleteZip(string zipPath)
    {
        try
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
        catch (Exception ex)
        {
            Log.Error(
                $"RcloneBackend: failed to delete temp zip '{zipPath}' - " +
                $"a plaintext copy of the backup remains on disk: {ex.Message}");
        }
    }

    /// <summary>
    /// How old a <c>claude-backup-*.zip</c> must be, by <see cref="File.GetLastWriteTimeUtc(string)"/>,
    /// before <see cref="SweepStaleZips"/> will even consider deleting it.
    /// Deliberately generous: this class's zip file names now carry a GUID
    /// (see <see cref="Run"/>), which means a naive "delete anything
    /// matching the pattern" sweep would just as happily delete a
    /// CONCURRENTLY RUNNING instance's own in-flight archive - e.g. a
    /// scheduled run racing a tray "back up now" button - as a genuinely
    /// abandoned one. A real backup run finishes in seconds to at most a few
    /// minutes, so anything still there a full day later cannot plausibly
    /// belong to a live run; anything younger is left alone even if it
    /// really is stale, because the cost of leaving it an extra sweep cycle
    /// is nothing (the owning run's own <see cref="DeleteZip"/> handles the
    /// common case) while the cost of guessing wrong is deleting bytes out
    /// from under an active upload.
    /// </summary>
    private static readonly TimeSpan StaleZipAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Best-effort cleanup of any zip left behind by a previous run whose
    /// own delete failed (see <see cref="DeleteZip"/>). Runs at the start of
    /// every <see cref="Run"/> call rather than relying on any external
    /// sweep, since nothing else in this tool ever revisits <c>tempDir</c>.
    /// Conservative on two independent axes so it cannot become the cross-
    /// run deletion hazard it is meant to clean up after: an age gate (see
    /// <see cref="StaleZipAge"/>) skips anything that could plausibly belong
    /// to a still-running instance, and a per-file lock check skips
    /// anything still open (whether or not it is old enough) instead of
    /// letting one locked file abort the whole sweep. A failure anywhere in
    /// here - enumerating the directory, stat'ing a file, an unexpected
    /// exception from a single delete - is logged at Warn and swallowed: a
    /// housekeeping step must never take down a backup.
    /// </summary>
    private void SweepStaleZips()
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - StaleZipAge;
            var removed = 0;

            foreach (var candidate in Directory.EnumerateFiles(_tempDir, "claude-backup-*.zip"))
            {
                try
                {
                    // Too young to safely assume this is not a concurrently
                    // running instance's own in-flight zip - leave it; the
                    // owning run's own finally block is responsible for it.
                    if (File.GetLastWriteTimeUtc(candidate) > cutoffUtc)
                        continue;

                    File.Delete(candidate);
                    removed++;
                }
                catch (IOException)
                {
                    // Still locked (most plausibly a live run's own zip that
                    // happens to be old enough to pass the age gate, or an
                    // AV scanner/indexer) - skip it quietly rather than
                    // treating a normal race as an error worth logging.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same reasoning as the IOException case above.
                }
            }

            if (removed > 0)
            {
                Log.Info(
                    $"RcloneBackend: swept {removed} stale temp zip(s) older than " +
                    $"{StaleZipAge.TotalHours:0} hour(s) from '{_tempDir}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"RcloneBackend: failed to sweep stale temp zips in '{_tempDir}': {ex.Message}");
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

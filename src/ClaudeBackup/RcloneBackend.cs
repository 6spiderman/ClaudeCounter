using System.IO.Compression;
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
/// does, the failure is logged loudly rather than swallowed, and the next
/// call to Run sweeps any such leftover away before writing a new one.
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
    /// Best-effort cleanup of any zip left behind by a previous run whose
    /// own delete failed (see <see cref="DeleteZip"/>). Runs at the start of
    /// every <see cref="Run"/> call rather than relying on any external
    /// sweep, since nothing else in this tool ever revisits <c>tempDir</c>.
    /// A failure to sweep is logged and otherwise ignored - it must never
    /// block the current run's own backup from proceeding.
    /// </summary>
    private void SweepStaleZips()
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(_tempDir, "claude-backup-*.zip"))
            {
                try
                {
                    File.Delete(stale);
                    Log.Warn($"RcloneBackend: swept stale temp zip left over from a previous run: '{stale}'.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"RcloneBackend: failed to sweep stale temp zip '{stale}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"RcloneBackend: failed to enumerate '{_tempDir}' for stale-zip sweep: {ex.Message}");
        }
    }

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

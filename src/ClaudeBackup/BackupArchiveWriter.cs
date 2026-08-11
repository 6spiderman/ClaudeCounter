using System.IO.Compression;
using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Shared "snapshot a file selection into a claude-backup-*.zip" logic (S14:
/// extracted out of what used to be RcloneBackend.Run's own zip-building
/// code, so <see cref="RcloneBackend"/> and <see cref="SyncFolderBackend"/>
/// share exactly one implementation instead of two copies that could drift
/// apart). Only the actual upload/copy step differs between transports -
/// zip naming/timestamping, the <see cref="RelativePathGuard.IsSafe"/> and
/// <see cref="SecretDenylist.IsSecret"/> rejections, the duplicate-entry
/// rejection, and the temp-zip hygiene (sweep-stale-zips-on-the-way-in,
/// always-delete-on-the-way-out) are identical for both, so this is the ONE
/// place that logic lives.
///
/// The archive name format ("claude-backup-{yyyyMMdd-HHmmss}-{guid:N}.zip")
/// is pinned: <see cref="DriveRetention.IsOurs"/> matches on exactly this
/// scheme, and DriveRetentionTests.IsOursMatchesExactlyTheNamingSchemeRcloneBackendWrites
/// asserts it directly - <see cref="BuildZipPath"/> must never change
/// independently of that test. RcloneBackend's own behaviour (including
/// every string this class can produce in a log line) is preserved
/// byte-for-byte across this extraction - every caller passes its own class
/// name as <paramref name="callerName"/>-equivalent so RcloneBackend's log
/// lines still read "RcloneBackend: ..." exactly as before.
/// </summary>
internal static class BackupArchiveWriter
{
    /// <summary>
    /// Unique per call (timestamp + a GUID, not just a timestamp): two runs
    /// landing in the same second - e.g. a scheduled run racing a tray "back
    /// up now" - must never collide on a zip file name. A collision would
    /// make <see cref="CreateArchive"/> throw for the second run and, worse,
    /// would mean the finally block's <see cref="DeleteZip"/> could delete
    /// the FIRST run's zip out from under an in-flight upload.
    /// </summary>
    internal static string BuildZipPath(string tempDir) =>
        Path.Combine(tempDir, $"claude-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");

    /// <summary>
    /// Writes <paramref name="files"/> (root-relative to <paramref
    /// name="sourceRoot"/>) into a new zip at <paramref name="zipPath"/>,
    /// applying the same three fail-closed backstops every backend needs at
    /// the actual point of write, since each backend's own Run is public API
    /// taking an arbitrary file list: an unsafe relative path (<see
    /// cref="RelativePathGuard.IsSafe"/>), a secret-shaped file name (<see
    /// cref="SecretDenylist.IsSecret"/>), and a duplicate zip entry name are
    /// all refused (logged, not thrown) rather than archived.
    /// </summary>
    internal static void CreateArchive(string zipPath, string sourceRoot, IReadOnlyList<string> files, string callerName)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in files)
        {
            if (!RelativePathGuard.IsSafe(rel))
            {
                Log.Warn($"{callerName}: refusing unsafe relative path '{rel}'.");
                continue;
            }

            if (SecretDenylist.IsSecret(rel))
            {
                Log.Warn($"{callerName}: refusing to archive secret-named file (denylist backstop): '{rel}'.");
                continue;
            }

            if (!seenEntries.Add(rel))
            {
                Log.Warn($"{callerName}: refusing duplicate zip entry name: '{rel}'.");
                continue;
            }

            var src = Path.Combine(sourceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(src))
                zip.CreateEntryFromFile(src, rel);
            else
                Log.Warn($"{callerName}: source file missing, skipping: '{rel}'.");
        }
    }

    /// <summary>
    /// Deletes the just-written temp zip. A failure here is the one failure
    /// that most needs to be visible: the file left behind is a plaintext
    /// copy of the user's Claude config, and nothing else will ever clean it
    /// up except the next call's <see cref="SweepStaleZips"/>. Does not
    /// rethrow - the caller's own result (success or a specific
    /// upload/copy failure) must not be replaced by a cleanup failure - but
    /// logs loudly at Error level naming the exact path.
    /// </summary>
    internal static void DeleteZip(string zipPath, string callerName)
    {
        try
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
        catch (Exception ex)
        {
            Log.Error(
                $"{callerName}: failed to delete temp zip '{zipPath}' - " +
                $"a plaintext copy of the backup remains on disk: {ex.Message}");
        }
    }

    /// <summary>
    /// How old a <c>claude-backup-*.zip</c> must be, by <see cref="File.GetLastWriteTimeUtc(string)"/>,
    /// before <see cref="SweepStaleZips"/> will even consider deleting it.
    /// Deliberately generous: the zip file name carries a GUID (see <see
    /// cref="BuildZipPath"/>), which means a naive "delete anything matching
    /// the pattern" sweep would just as happily delete a CONCURRENTLY
    /// RUNNING instance's own in-flight archive - e.g. a scheduled run
    /// racing a tray "back up now" - as a genuinely abandoned one. A real
    /// backup run finishes in seconds to at most a few minutes, so anything
    /// still there a full day later cannot plausibly belong to a live run;
    /// anything younger is left alone even if it really is stale, because
    /// the cost of leaving it an extra sweep cycle is nothing (the owning
    /// run's own <see cref="DeleteZip"/> handles the common case) while the
    /// cost of guessing wrong is deleting bytes out from under an active
    /// upload.
    /// </summary>
    internal static readonly TimeSpan StaleZipAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Best-effort cleanup of any zip left behind by a previous run whose own
    /// delete failed (see <see cref="DeleteZip"/>). Callers run this at the
    /// start of every write, since nothing else ever revisits <paramref
    /// name="tempDir"/>. Conservative on two independent axes so it cannot
    /// become the cross-run deletion hazard it is meant to clean up after: an
    /// age gate (see <see cref="StaleZipAge"/>) skips anything that could
    /// plausibly belong to a still-running instance, and a per-file lock
    /// check skips anything still open (whether or not it is old enough)
    /// instead of letting one locked file abort the whole sweep. A failure
    /// anywhere in here - enumerating the directory, stat'ing a file, an
    /// unexpected exception from a single delete - is logged at Warn and
    /// swallowed: a housekeeping step must never take down a backup.
    /// </summary>
    internal static void SweepStaleZips(string tempDir, string callerName)
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - StaleZipAge;
            var removed = 0;

            foreach (var candidate in Directory.EnumerateFiles(tempDir, "claude-backup-*.zip"))
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
                    $"{callerName}: swept {removed} stale temp zip(s) older than " +
                    $"{StaleZipAge.TotalHours:0} hour(s) from '{tempDir}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{callerName}: failed to sweep stale temp zips in '{tempDir}': {ex.Message}");
        }
    }
}

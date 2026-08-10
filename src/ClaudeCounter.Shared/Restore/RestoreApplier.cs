using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Applies a chosen subset of classified entries (design spec step 6) to
/// live ~/.claude, after taking the safety copy restore rule 2 requires.
/// Pure I/O, no WinForms dependency - safe to run off the UI thread.
///
/// Every one of the four write-time safety rules is re-checked HERE, at the
/// actual point bytes reach the live tree, regardless of what already ran
/// during materialisation - mirroring GitBackend.MirrorFiles and
/// RcloneBackend's own point-of-write backstops, which re-check containment
/// and the denylist even though their own callers already filtered upstream:
///   - Rule 2 (safety copy): any file this call is about to overwrite is
///     copied to the safety folder first - determined by actually checking
///     whether the live file exists right now, not by trusting a possibly-
///     stale RestoreFileStatus.New/Changed classification from an earlier
///     preview call.
///   - Rule 3 (never delete / never touch LiveOnly): an entry whose status
///     is not New or Changed is skipped, however <paramref name="chosen"/>
///     was assembled by the caller.
///   - Rule 4 (denylist backstop): re-applied against every relative path
///     before it is written.
///   - Rule 5 (containment): the resolved live-side full path is verified to
///     still be under <c>liveRoot</c> before anything is written.
/// </summary>
public static class RestoreApplier
{
    /// <summary>
    /// Applies <paramref name="chosen"/> from <paramref name="stagedRoot"/>
    /// onto <paramref name="liveRoot"/>. <paramref name="safetyBaseDir"/> is
    /// the parent of the timestamped safety folder (e.g.
    /// <c>%LOCALAPPDATA%\ClaudeCounter\restore-safety</c>) - a new
    /// <c>&lt;timestamp&gt;</c> subfolder is created per call, and its full
    /// path is returned as <see cref="RestoreApplyResult.SafetyCopyPath"/> so
    /// the caller can log and show it (only non-null when at least one file
    /// was actually overwritten - nothing was created to protect otherwise).
    /// <paramref name="now"/> defaults to the real clock; a test supplies a
    /// fixed value so the timestamp folder name is deterministic.
    /// </summary>
    public static RestoreApplyResult Apply(
        string stagedRoot,
        string liveRoot,
        IReadOnlyList<RestoreFileEntry> chosen,
        string safetyBaseDir,
        DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss");
        var safetyDir = Path.Combine(safetyBaseDir, timestamp);

        var liveRootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(liveRoot));

        var written = new List<string>();
        var skipped = new List<string>();
        var created = 0;
        var overwritten = 0;
        var safetyCopiesTaken = 0;

        try
        {
            foreach (var entry in chosen)
            {
                // Rule 3 backstop: LiveOnly (and Identical - nothing to do)
                // are never applied, however `chosen` was assembled.
                if (entry.Status is not (RestoreFileStatus.New or RestoreFileStatus.Changed))
                {
                    Log.Warn($"RestoreApplier: refusing to apply '{entry.RelativePath}' with status {entry.Status} (never New/Changed).");
                    skipped.Add(entry.RelativePath);
                    continue;
                }

                if (!RelativePathGuard.IsSafe(entry.RelativePath))
                {
                    Log.Warn($"RestoreApplier: refusing unsafe relative path '{entry.RelativePath}'.");
                    skipped.Add(entry.RelativePath);
                    continue;
                }

                // Rule 4 backstop: even though every materialisation path
                // (RestoreZipSource.ExtractSafely / RestoreGitSource.CopySafely)
                // already filters SecretDenylist on the way in, this is the
                // last point before a byte reaches the live tree.
                if (SecretDenylist.IsSecret(entry.RelativePath))
                {
                    Log.Warn($"RestoreApplier: refusing to restore denylisted file '{entry.RelativePath}'.");
                    skipped.Add(entry.RelativePath);
                    continue;
                }

                var relForFs = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var srcPath = Path.Combine(stagedRoot, relForFs);
                var liveFullPath = Path.GetFullPath(Path.Combine(liveRoot, relForFs));

                // Rule 5 backstop: never write outside liveRoot.
                if (!RelativePathGuard.IsWithinDirectory(liveRootFull, liveFullPath))
                {
                    Log.Warn($"RestoreApplier: refusing path that resolves outside the live root: '{entry.RelativePath}'.");
                    skipped.Add(entry.RelativePath);
                    continue;
                }

                if (!File.Exists(srcPath))
                {
                    Log.Warn($"RestoreApplier: staged file missing, skipping: '{entry.RelativePath}'.");
                    skipped.Add(entry.RelativePath);
                    continue;
                }

                // Rule 2: determined by the live file's ACTUAL current
                // existence, not by trusting entry.Status - a file the
                // preview saw as "New" could have appeared on disk since
                // (another process, a concurrent restore), and one it saw as
                // "Changed" could have vanished. Either way, "does this write
                // overwrite something that exists right now" is what decides
                // whether a safety copy is needed, and the safety copy always
                // happens before the write that would destroy it.
                if (File.Exists(liveFullPath))
                {
                    var safetyPath = Path.Combine(safetyDir, relForFs);
                    Directory.CreateDirectory(Path.GetDirectoryName(safetyPath)!);
                    File.Copy(liveFullPath, safetyPath, overwrite: true);
                    safetyCopiesTaken++;
                    overwritten++;
                }
                else
                {
                    created++;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(liveFullPath)!);
                File.Copy(srcPath, liveFullPath, overwrite: true);
                written.Add(entry.RelativePath);
                Log.Info($"RestoreApplier: wrote '{entry.RelativePath}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"RestoreApplier: apply failed after writing {written.Count} file(s): {ex.Message}");
            return new RestoreApplyResult(
                false, ex.Message,
                safetyCopiesTaken > 0 ? safetyDir : null,
                created, overwritten, written, skipped);
        }

        if (safetyCopiesTaken > 0)
            Log.Info($"RestoreApplier: safety copy of {safetyCopiesTaken} file(s) saved to '{safetyDir}'.");

        return new RestoreApplyResult(
            true,
            $"Restored {written.Count} file(s) ({created} new, {overwritten} overwritten).",
            safetyCopiesTaken > 0 ? safetyDir : null,
            created, overwritten, written, skipped);
    }
}

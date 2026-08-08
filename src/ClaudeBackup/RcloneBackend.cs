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
/// this at a private location under %LOCALAPPDATA%\ClaudeCounter\) and is
/// ALWAYS deleted before <see cref="Run"/> returns - on success, on an
/// upload failure, or on an exception thrown while zipping - because a
/// leftover zip is a plaintext copy of the user's Claude config sitting on
/// disk. Every string that can reach a log line, an exception message, or a
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

        var zipPath = Path.Combine(_tempDir, $"claude-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            Directory.CreateDirectory(_tempDir);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var rel in files)
                {
                    // Defense in depth, matching GitBackend's discipline:
                    // FileSelector is expected to hand back root-relative,
                    // forward-slashed paths with no ".." segment, but this
                    // backend must not assume that.
                    if (!IsSafeRelativePath(rel))
                    {
                        Log.Warn($"RcloneBackend: refusing unsafe relative path '{rel}'.");
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
            // Best-effort but unconditional: this runs whether Run is about
            // to return success, a failure, or propagate out of the try via
            // an exception that Fail below did not see (there is none here,
            // but the discipline is "always attempt cleanup" regardless of
            // how the try block exits).
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>
    /// Rejects absolute paths, backslashes, and "." / ".." segments so a
    /// malformed relative path can never be written into the zip under an
    /// unexpected entry name. Mirrors <c>GitBackend.IsSafeRelativePath</c>.
    /// </summary>
    private static bool IsSafeRelativePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (Path.IsPathRooted(rel)) return false;
        if (rel.Contains('\\')) return false;

        foreach (var segment in rel.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }
        return true;
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

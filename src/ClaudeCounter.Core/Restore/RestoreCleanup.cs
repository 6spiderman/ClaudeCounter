using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Best-effort cleanup for restore-owned scratch directories - a
/// materialised staging tree (<see cref="RestoreZipSource.Materialize"/> /
/// <see cref="RestoreGitSource.Materialize"/>'s <c>destinationDir</c>) and
/// RestoreGitSource's own clone (its <c>stagingDir</c> constructor
/// parameter). Fix round 1, Minor: both are, like RcloneBackend's temp zip,
/// a plaintext copy of (a subset of) the user's Claude config sitting on
/// disk once a restore session is done with them - but unlike the temp zip,
/// nothing in this layer deletes either automatically, since a caller may
/// legitimately want a materialised snapshot to stick around across several
/// preview/apply cycles. This exists so the future restore dialog has
/// somewhere to call at the end of a session, rather than reaching for a raw
/// <see cref="Directory.Delete(string, bool)"/> itself and needing to
/// reinvent the same "log loudly on failure, never throw" discipline used
/// everywhere else in this codebase for exactly this kind of cleanup (see
/// RcloneBackend.DeleteZip).
/// </summary>
public static class RestoreCleanup
{
    /// <summary>
    /// Deletes <paramref name="path"/> recursively if it exists. Never
    /// throws - a cleanup failure (a file locked by an AV scanner or
    /// indexer, or still open) is logged loudly rather than swallowed
    /// silently, since the directory being cleaned up can contain a
    /// plaintext copy of (a subset of) the user's Claude config, but a
    /// cleanup failure must not take down whatever the caller does next.
    /// </summary>
    public static void DeleteStagingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"RestoreCleanup: failed to delete staging directory '{path}': {ex.Message}");
        }
    }
}

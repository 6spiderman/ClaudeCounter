namespace ClaudeBackup;

/// <summary>
/// The three folder-path preconditions for the sync-folder backup transport
/// (see <see cref="DriveTransport.SyncFolder"/> and <c>SyncFolderBackend</c>'s
/// own doc comment for the full rationale): non-blank, rooted (<see
/// cref="Path.IsPathRooted(string)"/> - a UNC path such as
/// "\\nas\share\claude" is explicitly valid, since a NAS share is a
/// first-class target for this transport, not an edge case), and not
/// overlapping the backup source root (via <see cref="RelativePathGuard.Overlaps"/>) -
/// without that last guard the archive would land inside the tree being
/// backed up and every subsequent run would sweep up its own predecessors.
///
/// Lives in ClaudeCounter.Shared (not ClaudeBackup.csproj, where
/// <c>SyncFolderBackend</c> itself lives) for the same reason
/// <see cref="DriveRetention"/> was moved here: the Settings dialog
/// (ClaudeCounter.csproj) has no ProjectReference to ClaudeBackup.csproj -
/// see SettingsForm's own doc comment on why not, it would drag the worker's
/// RID-specific publish graph into the tray's single-file publish - but
/// still needs to run this exact validation at Save time, using the exact
/// same rules <c>SyncFolderBackend.Run</c> itself enforces at write time,
/// not a second, divergent copy of them. <c>SyncFolderBackend.ValidateFolderPath</c>
/// now simply forwards to <see cref="Validate"/> so
/// <c>SyncFolderBackendTests</c>' existing direct calls keep compiling and
/// passing unchanged.
/// </summary>
public static class SyncFolderPathValidator
{
    /// <summary>
    /// Returns null when <paramref name="folderPath"/> is valid, or the exact
    /// failure message a caller should show/fail with. Side-effect-free (no
    /// filesystem I/O beyond path-string normalisation via
    /// <see cref="Path.GetFullPath(string)"/>, no network access even for a
    /// UNC path) so it is directly unit-testable without a real NAS share or
    /// a real directory on disk.
    /// </summary>
    public static string? Validate(string folderPath, string sourceRoot)
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
}

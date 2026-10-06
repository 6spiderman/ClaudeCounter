namespace ClaudeBackup;

/// <summary>
/// Pure, fully unit-testable guess at which named sync-folder destination a
/// <see cref="DriveTarget.FolderPath"/> belongs to, for DISPLAY ONLY (S16
/// design: named backup destinations). Used by SettingsForm to pick a
/// sensible initial "Back up to" selection when an existing backup.json has
/// <see cref="DriveTransport.SyncFolder"/> but <see cref="SyncProvider"/> is
/// still <see cref="SyncProvider.Other"/> - either genuinely unrecognisable,
/// or written by the pre-S16 build that had Transport/FolderPath but no
/// SyncProvider property at all. Never changes what actually gets backed up
/// or where - only SyncFolderBackend's own use of FolderPath does that; this
/// result is not written back to backup.json just from inferring it (see
/// SyncProvider's own doc comment).
///
/// String-pattern matching only - no environment variable, registry, or
/// filesystem access - so this is directly testable without a real machine
/// and without needing to inject anything.
/// </summary>
public static class SyncProviderInference
{
    /// <summary>
    /// Order matters: a UNC path is checked FIRST, per the design brief's own
    /// rule of thumb ("UNC means NAS") - a NAS share's folder could
    /// coincidentally be named after another provider (e.g. a share called
    /// "OneDrive-Archive"), and UNC-ness is a far stronger, unambiguous
    /// signal for "this is a network share" than a substring match is for
    /// any of the sync-client providers below it.
    /// </summary>
    public static SyncProvider InferFromPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return SyncProvider.Other;

        if (folderPath.StartsWith(@"\\", StringComparison.Ordinal))
            return SyncProvider.Nas;

        if (ContainsSegment(folderPath, "onedrive"))
            return SyncProvider.OneDrive;

        // Covers both the fixed "%USERPROFILE%\Google Drive" location and
        // Google Drive for Desktop's virtual "<drive>:\My Drive" mount - see
        // SyncFolderScanner.DetectFrom, which offers exactly these two
        // shapes.
        if (ContainsSegment(folderPath, "google drive") || ContainsSegment(folderPath, "my drive"))
            return SyncProvider.GoogleDrive;

        if (ContainsSegment(folderPath, "dropbox"))
            return SyncProvider.Dropbox;

        return SyncProvider.Other;
    }

    private static bool ContainsSegment(string path, string needle) =>
        path.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

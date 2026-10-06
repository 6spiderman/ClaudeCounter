using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// S17c: pure, testable logic behind the destinations dialog's "Add..." kind
/// picker and default naming - public and static (no Form), for the same
/// reason as SettingsForm.InitialDestinationIndex/HasEmbeddedCredential: this
/// project's tests never construct a Form except via the dedicated STA
/// smoke-test helper.
/// </summary>
public static class BackupDestinationNaming
{
    /// <summary>
    /// One entry per item the "Add..." kind picker (<see
    /// cref="BackupDestinationKindDialog"/>) offers. Several destinations of
    /// the SAME kind are explicitly allowed (two NAS shares, two GitHub
    /// repos) - nothing here enforces one-per-kind, unlike the old S16
    /// "Back up to" selector this replaces.
    /// </summary>
    public readonly record struct KindOption(string Label, DestinationKind Kind, SyncProvider Provider);

    public static readonly KindOption[] KindOptions =
    [
        new("GitHub", DestinationKind.GitHub, SyncProvider.Other),
        new("Google Drive (sync folder)", DestinationKind.SyncFolder, SyncProvider.GoogleDrive),
        new("OneDrive (sync folder)", DestinationKind.SyncFolder, SyncProvider.OneDrive),
        new("Dropbox (sync folder)", DestinationKind.SyncFolder, SyncProvider.Dropbox),
        new("NAS / network share", DestinationKind.SyncFolder, SyncProvider.Nas),
        new("rclone remote (advanced)", DestinationKind.Rclone, SyncProvider.Other),
    ];

    /// <summary>
    /// The clean display name for a destination's Kind/SyncProvider pair -
    /// used both as the base for a newly-added destination's default Name
    /// (see <see cref="GenerateUniqueName"/>) and as the "Kind" column shown
    /// by the destinations dialog's list and the Backup tab's read-only
    /// summary. Deliberately WITHOUT the picker's own parenthetical
    /// qualifiers ("(sync folder)", "(advanced)") - those exist to explain
    /// the choice at pick time, not to appear inside a destination's own
    /// name or a status column.
    /// </summary>
    public static string KindDisplayName(DestinationKind kind, SyncProvider provider) => kind switch
    {
        DestinationKind.GitHub => "GitHub",
        DestinationKind.SyncFolder => provider switch
        {
            SyncProvider.GoogleDrive => "Google Drive",
            SyncProvider.OneDrive => "OneDrive",
            SyncProvider.Dropbox => "Dropbox",
            SyncProvider.Nas => "NAS / network share",
            _ => "Sync folder",
        },
        DestinationKind.Rclone => "rclone remote",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Picks a default Name for a newly-added destination that is
    /// distinguishable from every destination already configured - two NAS
    /// shares or two GitHub repos must both be addable with names that do
    /// not collide. Returns the plain kind name ("GitHub") when it is not
    /// already taken, otherwise the first "GitHub (2)", "GitHub (3)", ... not
    /// already taken. Comparison is case-insensitive, matching every other
    /// user-visible-string comparison in this dialog family (e.g.
    /// RestoreDestinationModel).
    /// </summary>
    public static string GenerateUniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
            return baseName;

        var suffix = 2;
        while (existing.Contains($"{baseName} ({suffix})"))
            suffix++;
        return $"{baseName} ({suffix})";
    }
}

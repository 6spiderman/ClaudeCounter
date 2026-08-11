using System.Text.Json;

namespace ClaudeBackup;

public sealed class GitTarget
{
    public bool Enabled { get; set; }
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "main";

    /// <summary>GitHub's own include/exclude selection - independent of <see cref="DriveTarget"/>'s.</summary>
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
}

/// <summary>
/// Which mechanism <see cref="DriveTarget"/> uploads through (S14 design
/// doc: "sync-folder backup transport"). A switch on the SAME destination,
/// not a third destination - see the design doc's "Key structural decision"
/// for why: BackupRunResult/BackupStatus/BackupStatusWriter/BackupHealth/
/// BackupRunner/RestoreDialog all encode exactly two destinations as
/// positional fields, and a third would fan out across every one of them.
/// <see cref="Rclone"/> = 0 is the default specifically so a backup.json
/// written before this field existed loads with Transport already correct -
/// a property absent from old JSON deserializing to its type default is
/// exactly the intended back-compat behaviour, which is also why this change
/// does not bump <see cref="BackupConfig.CurrentBackupConfigVersion"/>.
/// </summary>
public enum DriveTransport
{
    /// <summary>Upload via the rclone binary to <see cref="DriveTarget.RcloneRemote"/> - unchanged, original behaviour.</summary>
    Rclone = 0,

    /// <summary>
    /// Copy the archive straight into <see cref="DriveTarget.FolderPath"/> -
    /// a plain folder a sync client (Google Drive for Desktop, OneDrive,
    /// Dropbox) or a NAS share already watches/replicates on its own. No
    /// external binary, no authentication: the sync client (or the NAS
    /// itself) does the actual upload.
    /// </summary>
    SyncFolder = 1,
}

/// <summary>
/// Which named sync-folder destination <see cref="DriveTarget.FolderPath"/>
/// belongs to (S16 design: named backup destinations) - a pure DISPLAY
/// discriminator. Never read by BackupRunner or SyncFolderBackend, which
/// only ever look at <see cref="DriveTarget.Transport"/> and <see
/// cref="DriveTarget.FolderPath"/>; this exists purely so the Backup tab's
/// "Back up to" selector can show "OneDrive" (or "NAS / network share", or
/// "Dropbox", or "Google Drive") again the next time Settings opens, instead
/// of a meaningless generic "sync folder". <see cref="Other"/> = 0 is the
/// default specifically so a backup.json written before this field existed -
/// including one written by the 1.2 sync-folder-transport build, which had
/// Transport and FolderPath but not this - loads with SyncProvider already
/// correct (back-compat): an absent property deserializing to its type
/// default is exactly the intended behaviour, which is also why this does
/// not bump <see cref="BackupConfig.CurrentBackupConfigVersion"/>.
/// SettingsForm infers a display-only guess from FolderPath (see
/// SyncProviderInference) when Transport is SyncFolder but this is still
/// Other, rather than showing the user a meaningless label - but that
/// inference is never written back to this field just from opening and
/// re-saving Settings; it only changes when the user actually picks a named
/// destination in the selector.
/// </summary>
public enum SyncProvider
{
    Other = 0,
    GoogleDrive = 1,
    OneDrive = 2,
    Dropbox = 3,
    Nas = 4,
}

public sealed class DriveTarget
{
    public bool Enabled { get; set; }
    public string RcloneRemote { get; set; } = "";

    /// <summary>Which transport this destination uses - see <see cref="DriveTransport"/>. Defaults to <see cref="DriveTransport.Rclone"/> so an existing config keeps behaving exactly as before.</summary>
    public DriveTransport Transport { get; set; } = DriveTransport.Rclone;

    /// <summary>
    /// Destination folder for the <see cref="DriveTransport.SyncFolder"/>
    /// transport. A SEPARATE field from <see cref="RcloneRemote"/> on
    /// purpose - the two have different validation rules (this one is a
    /// filesystem path checked for rootedness and for not overlapping <see
    /// cref="BackupConfig.SourceRoot"/>; the rclone remote is checked for a
    /// leading dash and an embedded credential, neither of which applies
    /// here since no process arguments are involved) - conflating the two
    /// fields would mean applying the wrong checks, or none at all. A UNC
    /// path (e.g. "\\nas\share\claude") is explicitly a valid value - a NAS
    /// share is a first-class target for this transport, not an edge case.
    /// </summary>
    public string FolderPath { get; set; } = "";

    /// <summary>Which named sync-folder destination <see cref="FolderPath"/> belongs to - see <see cref="SyncProvider"/>. Only meaningful when <see cref="Transport"/> is <see cref="DriveTransport.SyncFolder"/>; every backend ignores it and reads <see cref="FolderPath"/> directly.</summary>
    public SyncProvider SyncProvider { get; set; } = SyncProvider.Other;

    /// <summary>Drive's own include/exclude selection - independent of <see cref="GitTarget"/>'s.</summary>
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();

    /// <summary>
    /// Keep at most this many <c>claude-backup-*.zip</c> entries in <see
    /// cref="RcloneRemote"/>, pruning the oldest first - null (the default)
    /// means this rule is off. Independently switchable from <see
    /// cref="DeleteOlderThanDays"/>; when both are set a zip is pruned if
    /// EITHER rule would remove it (the union), per the design spec. See
    /// DriveRetention.SelectForDeletion for the pure selection logic, and
    /// RcloneBackend for where it is applied - only after a successful
    /// upload, and never down to zero remaining backups.
    /// </summary>
    public int? KeepLastCount { get; set; }

    /// <summary>
    /// Delete <c>claude-backup-*.zip</c> entries in <see cref="RcloneRemote"/>
    /// older than this many days - null (the default) means this rule is
    /// off. See <see cref="KeepLastCount"/>'s doc comment for how the two
    /// combine.
    /// </summary>
    public int? DeleteOlderThanDays { get; set; }
}

public sealed class ScheduleConfig
{
    public string Frequency { get; set; } = "daily"; // daily | weekly | hourly
    public string Time { get; set; } = "09:00";

    // Advanced Task Scheduler settings (schedule-robustness spec). Windows'
    // own defaults for the two battery settings are both true, which on a
    // laptop means the backup only ever runs while plugged in - exactly the
    // silent-skip behaviour StartWhenAvailable exists to fix. Defaulting
    // them false here is deliberate; do not "correct" them to match Windows.

    /// <summary>Run a missed backup as soon as possible once the machine is available again.</summary>
    public bool StartWhenAvailable { get; set; } = true;

    /// <summary>Only start the task when a network connection is available.</summary>
    public bool RunOnlyIfNetworkAvailable { get; set; } = true;

    /// <summary>Deliberately false by default - see this class's remarks.</summary>
    public bool DisallowStartIfOnBatteries { get; set; }

    /// <summary>Deliberately false by default - see this class's remarks.</summary>
    public bool StopIfGoingOnBatteries { get; set; }

    /// <summary>Restart the task after a failed run.</summary>
    public bool RestartOnFailure { get; set; } = true;

    /// <summary>Minutes between restart attempts when <see cref="RestartOnFailure"/> is set.</summary>
    public int RestartIntervalMinutes { get; set; } = 15;

    /// <summary>Maximum number of restart attempts when <see cref="RestartOnFailure"/> is set.</summary>
    public int RestartCount { get; set; } = 3;

    /// <summary>
    /// Days after a destination's newest recorded success before
    /// BackupHealth.Evaluate considers that destination Stale. 0 is a valid,
    /// meaningful setting - "never warn about staleness" - not "unset"; see
    /// BackupConfig.Normalize, which must not coerce it to the default
    /// (mirrors AppSettings.PopupAutoDismissSeconds, which has the identical
    /// 0-is-meaningful shape). Default 3: a daily schedule plus an ordinary
    /// weekend-off laptop should not trip this, but a genuinely dead backup
    /// still surfaces within a few days (design spec).
    /// </summary>
    public int BackupStaleAfterDays { get; set; } = 3;
}

public sealed class BackupConfig
{
    public string SourceRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Legacy pre-per-destination include/exclude selection, shared by both
    /// destinations before <see cref="GitTarget.Include"/> / <see
    /// cref="DriveTarget.Include"/> existed. Deserialize-only: kept on the
    /// type purely so a backup.json written before <see
    /// cref="BackupConfigVersion"/> 1 still parses instead of throwing out of
    /// <see cref="Load"/>. <see cref="Load"/> migrates a non-empty legacy
    /// selection onto BOTH <see cref="Github"/> and <see cref="Drive"/> the
    /// first time such a file is read (see the private migration helper) and
    /// clears these two lists as part of that. Never populated by <see
    /// cref="Default"/>, and never intentionally written to again after
    /// migration - do not read these directly; use <see
    /// cref="GitTarget.Include"/> / <see cref="DriveTarget.Include"/>
    /// instead.
    /// </summary>
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();

    public GitTarget Github { get; set; } = new();
    public DriveTarget Drive { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>
    /// Schema version for where Include/Exclude live. 0 (the type default)
    /// means "written before per-destination selection existed" - Include/
    /// Exclude lived directly on BackupConfig, shared by both destinations.
    /// Bumped to <see cref="CurrentBackupConfigVersion"/> by <see cref="Load"/>
    /// once a stale file has been migrated. Modeled on AppSettings'
    /// NotificationStateVersion.
    /// </summary>
    public int BackupConfigVersion { get; set; }

    /// <summary>Current schema version. Bump when the Include/Exclude shape changes again.</summary>
    public const int CurrentBackupConfigVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCounter", "backup.json");

    public static BackupConfig Default()
    {
        // plugins/*.json (not plugins/**/*.json): the only irreplaceable
        // state under plugins/ is the top-level manifests (installed_plugins.json,
        // known_marketplaces.json, blocklist.json). Everything nested one or
        // more directories deeper is re-downloadable third-party plugin
        // content, some of which carries secret-shaped keys (e.g. .mcp.json
        // "Authorization" fields) that the file-name denylist cannot see
        // inside. A user who wants the full plugin tree backed up can opt in
        // explicitly; it should not be a silent default.
        var include = new List<string> { "settings.json", "CLAUDE.md", "commands/**", "agents/**", "plugins/*.json" };
        // "**/*cache*" only ever constrains a file's own name (it does not
        // end in "**", so FileSelector will not prune a whole directory on
        // its account) and was measured to remove almost nothing on a real
        // machine, so it is gone entirely rather than left as dead weight -
        // "**/cache/**" is the pattern that actually prunes a cache
        // directory's contents.
        var exclude = new List<string> { "projects/**", "statsig/**", "**/cache/**" };

        // Both destinations get the SAME defaults today - a brand new user
        // has never configured anything to diverge, and giving GitHub and
        // Drive independent lists of the same content is what makes editing
        // just one of them later (via the Backup tab's destination selector)
        // a real, isolated change instead of secretly touching both.
        var result = new BackupConfig
        {
            // A freshly created config is already at the current shape, so a
            // later Load() of what this writes never mistakes it for a
            // pre-migration file (mirrors SettingsStore's fresh.Normalize()
            // stamping NotificationStateVersion before a fresh AppSettings'
            // first save, for the identical reason).
            BackupConfigVersion = CurrentBackupConfigVersion,
            Github = new GitTarget { Include = new List<string>(include), Exclude = new List<string>(exclude) },
            Drive = new DriveTarget { Include = new List<string>(include), Exclude = new List<string>(exclude) },
        };
        result.Normalize();
        return result;
    }

    public static BackupConfig Load(string path)
    {
        if (!File.Exists(path))
            return Default();
        try
        {
            var config = JsonSerializer.Deserialize<BackupConfig>(File.ReadAllText(path)) ?? Default();
            config.MigrateLegacySelection(path);
            config.Normalize();
            return config;
        }
        // Fix round 1 (Important 2): widened from JsonException/IOException.
        // UnauthorizedAccessException (an ACL-locked backup.json) does NOT
        // derive from IOException and was previously escaping this catch
        // entirely - see Program.RunWorker's remarks for why that mattered
        // (it is the root cause of Important 1). NotSupportedException can
        // come from JsonSerializer for a shape it cannot handle. Both are
        // exactly as "degrade to Default(), never throw" as a corrupt file.
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Default();
        }
    }

    /// <summary>
    /// Enforces field-level invariants that cannot be expressed as a plain
    /// property initializer - currently just clamping <see
    /// cref="ScheduleConfig.BackupStaleAfterDays"/> to a sane range. 0 ("never
    /// warn about staleness") is a meaningful in-range value - see that
    /// property's doc comment - so this is a plain clamp, not a
    /// fallback-to-default like AppSettings.Normalize's threshold handling.
    /// Called by both <see cref="Load"/> (so a hand-edited or corrupted
    /// backup.json can never carry an out-of-range value into
    /// BackupHealth.Evaluate) and <see cref="Default"/> (so a freshly created
    /// config is already normalized, mirroring SettingsStore's
    /// fresh.Normalize() pattern for AppSettings).
    /// </summary>
    public void Normalize()
    {
        Schedule ??= new();
        Schedule.BackupStaleAfterDays = Math.Clamp(Schedule.BackupStaleAfterDays, 0, 365);

        // S14: defensive null-guard for a JSON payload with an explicit null
        // FolderPath (rather than the field simply being absent) - same
        // pattern as every other string/list field this method and
        // MigrateLegacySelection already guard. An out-of-range Transport
        // (e.g. a hand-edited backup.json with "Transport": 99) is not a
        // value this enum ever produces itself, so it is treated the same
        // way an out-of-range BackupStaleAfterDays is above: reset to the
        // default (Rclone) rather than let an undefined enum value reach
        // BackupRunner's transport switch.
        Drive ??= new();
        Drive.FolderPath ??= "";
        if (!Enum.IsDefined(typeof(DriveTransport), Drive.Transport))
            Drive.Transport = DriveTransport.Rclone;

        // S16: same "an undefined enum value must never reach anything
        // downstream" guard as Transport just above - a hand-edited or
        // future-version backup.json with e.g. "SyncProvider": 99 is reset
        // to Other rather than left as a value this enum never produces.
        if (!Enum.IsDefined(typeof(SyncProvider), Drive.SyncProvider))
            Drive.SyncProvider = SyncProvider.Other;
    }

    /// <summary>
    /// One-time migration from the pre-per-destination shape: a backup.json
    /// at BackupConfigVersion 0 with a non-empty legacy Include/Exclude had
    /// that ONE list shared by both destinations. Copying it onto BOTH
    /// Github and Drive here preserves exactly what an existing user's
    /// backup used to select - nothing they configured changes meaning, on
    /// purpose; this is not the place to "improve" on it. Persists
    /// immediately so a second Load() of the same file sees
    /// BackupConfigVersion already current and never re-enters this method's
    /// copy branch.
    ///
    /// Gated on the legacy lists actually being non-empty, not just the
    /// version being stale: a file at version 0 with empty legacy lists (a
    /// user who cleared both boxes before this feature existed) has nothing
    /// to copy, and Github.Include/Drive.Include already deserialize to
    /// empty lists on their own (the type's field initializer), which is the
    /// exact same end state copying empty lists onto them would produce -
    /// so skipping the copy (and the version bump, and the save) in that
    /// case changes nothing observable.
    /// </summary>
    private void MigrateLegacySelection(string path)
    {
        // A JSON payload with an explicit null for any of these (rather than
        // the field simply being absent) would otherwise NRE below - see
        // AppSettings.NotificationState's "??= new()" for the same
        // defensive pattern after a real bug from exactly this shape.
        Include ??= new();
        Exclude ??= new();
        Github ??= new();
        Drive ??= new();
        Github.Include ??= new();
        Github.Exclude ??= new();
        Drive.Include ??= new();
        Drive.Exclude ??= new();
        Drive.FolderPath ??= ""; // S14: same defensive guard as Include/Exclude above, for an explicit JSON null

        if (BackupConfigVersion >= CurrentBackupConfigVersion)
            return;

        if (Include.Count > 0 || Exclude.Count > 0)
        {
            Github.Include = new List<string>(Include);
            Github.Exclude = new List<string>(Exclude);
            Drive.Include = new List<string>(Include);
            Drive.Exclude = new List<string>(Exclude);
            Include.Clear();
            Exclude.Clear();
            BackupConfigVersion = CurrentBackupConfigVersion;
            Save(path);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

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
///
/// S17a note: BackupRunResult/BackupStatus/BackupRunner/RestoreDialog are no
/// longer ALL fixed at exactly two - BackupConfig itself grew an arbitrary
/// <see cref="BackupConfig.Destinations"/> list, and BackupStatus/
/// BackupHealth/BackupStatusWriter now key off that list's ids instead of
/// two hardcoded names. BackupRunner and RestoreDialog have not been
/// migrated yet (S17b) - see <see cref="BackupConfig.Github"/>/<see
/// cref="BackupConfig.Drive"/> for the shim that keeps them compiling and
/// working unchanged in the meantime. This enum's own back-compat reasoning
/// above is unaffected either way.
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

/// <summary>
/// Which mechanism a <see cref="BackupDestination"/> uploads through - the
/// N-destination successor to the old "GitTarget vs DriveTarget(+Transport)"
/// split (S17a). GitHub keeps its own history via git commits, so retention
/// (<see cref="BackupDestination.KeepLastCount"/>/<see
/// cref="BackupDestination.DeleteOlderThanDays"/>) only ever applies to the
/// two zip-based kinds - see those properties' own doc comments for why that
/// is a convention enforced by nobody reading them for a GitHub-kind
/// destination, not a type-level split.
/// </summary>
public enum DestinationKind
{
    GitHub = 0,
    SyncFolder = 1,
    Rclone = 2,
}

/// <summary>
/// One backup destination (S17a: multi-destination model). Replaces the old
/// "exactly Github + Drive" shape - BackupConfig now carries an arbitrary
/// list of these, so the user can configure any combination of GitHub repos,
/// sync-folder destinations (OneDrive/Google Drive/Dropbox/NAS/plain
/// folder), and rclone remotes, including more than one of the same Kind
/// (two NAS shares, two GitHub repos) - nothing here enforces one-per-kind.
///
/// Deliberately one flat class covering all three kinds' connection fields
/// (RemoteUrl/Branch for GitHub, FolderPath/SyncProvider for SyncFolder,
/// RcloneRemote for Rclone) rather than a polymorphic/discriminated-union
/// shape - this mirrors how <see cref="DriveTarget"/> already carries both
/// the SyncFolder and Rclone connection fields on one class today,
/// discriminated by <see cref="DriveTransport"/>; extending that same idiom
/// to a third kind keeps JSON serialization simple (no type discriminator
/// machinery) and keeps every consumer's shape-checking identical to what it
/// already does for Transport.
/// </summary>
public sealed class BackupDestination
{
    /// <summary>
    /// Stable identifier - assigned once, at creation, and never changed
    /// afterward even if Name/Kind/connection fields change. This is the key
    /// BackupStatus's own per-destination status is stored under (see
    /// BackupStatus.Destinations), and what the "remove a destination" flow
    /// (S17c) uses to drop exactly one destination's status via
    /// BackupStatus.RemoveDestination without touching any other entry. The
    /// two destinations BackupConfig migrates out of the old Github/Drive
    /// shape get the WELL-KNOWN ids "github" and "drive" (see
    /// BackupConfig's migration) specifically so backup-status.json's own
    /// migration (BackupStatus.Load) can deterministically map its legacy
    /// top-level "Github"/"Drive" keys onto the same two ids - a status
    /// entry and its owning destination agree on the id without any
    /// matching-by-name heuristic. Every destination created after that
    /// (S17c's "add destination" flow) gets a generated id - see <see
    /// cref="NewId"/>.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>User-visible label - "GitHub", "OneDrive", "Work NAS", etc. Freely editable; never used as a lookup key (<see cref="Id"/> is).</summary>
    public string Name { get; set; } = "";

    public DestinationKind Kind { get; set; } = DestinationKind.Rclone;

    public bool Enabled { get; set; }

    /// <summary>GitHub connection field - meaningful only when <see cref="Kind"/> is <see cref="DestinationKind.GitHub"/>.</summary>
    public string RemoteUrl { get; set; } = "";

    /// <summary>GitHub connection field - meaningful only when <see cref="Kind"/> is <see cref="DestinationKind.GitHub"/>.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>SyncFolder connection field - meaningful only when <see cref="Kind"/> is <see cref="DestinationKind.SyncFolder"/>. See <see cref="DriveTarget.FolderPath"/>'s doc comment for the validation-rule reasoning, unchanged here.</summary>
    public string FolderPath { get; set; } = "";

    /// <summary>SyncFolder display discriminator - see <see cref="SyncProvider"/>. Meaningful only when <see cref="Kind"/> is <see cref="DestinationKind.SyncFolder"/>.</summary>
    public SyncProvider SyncProvider { get; set; } = SyncProvider.Other;

    /// <summary>Rclone connection field - meaningful only when <see cref="Kind"/> is <see cref="DestinationKind.Rclone"/>.</summary>
    public string RcloneRemote { get; set; } = "";

    /// <summary>This destination's own include/exclude selection - independent of every other destination's.</summary>
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();

    /// <summary>
    /// Keep at most this many <c>claude-backup-*.zip</c> entries, pruning the
    /// oldest first - null (the default) means this rule is off. Meaningful
    /// only for the zip-based kinds (<see cref="DestinationKind.SyncFolder"/>,
    /// <see cref="DestinationKind.Rclone"/>) - GitHub keeps its own history
    /// via commits, so pruning old zips is not a concept that applies to a
    /// GitHub-kind destination; this is a convention nothing enforces at the
    /// type level (see this class's own doc comment for why), mirroring how
    /// <see cref="DriveTarget"/> already carries fields that are only
    /// meaningful for one Transport value. See <see
    /// cref="DriveTarget.KeepLastCount"/>'s original doc comment for how this
    /// combines with <see cref="DeleteOlderThanDays"/> (union of either rule
    /// firing).
    /// </summary>
    public int? KeepLastCount { get; set; }

    /// <summary>Delete zip entries older than this many days - null (the default) means this rule is off. See <see cref="KeepLastCount"/>'s doc comment for scope and combination rules.</summary>
    public int? DeleteOlderThanDays { get; set; }

    /// <summary>A short, URL-safe, generated id for a newly created destination (S17c's "add destination" flow) - distinct from the two well-known "github"/"drive" ids the v1-&gt;v2 migration assigns.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
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

    /// <summary>
    /// S17a SHIM - kept only so BackupRunner/SettingsForm/RestoreDialog (out
    /// of scope for this task; S17b/S17c migrate them to read <see
    /// cref="Destinations"/> directly) keep compiling AND working with zero
    /// changes to those three files. <see cref="Destinations"/> - not this -
    /// is the real, authoritative, on-disk shape from
    /// BackupConfigVersion 2 onward: <see cref="Save"/> pushes whatever this
    /// property currently holds into the "github"-id entry of <see
    /// cref="Destinations"/> before serializing, and <see cref="Load"/> pulls
    /// it back out of that same entry afterward, so Destinations always wins
    /// on read and this is always what actually gets persisted on write. Do
    /// not add new production reads of this property outside the
    /// not-yet-migrated three files above - read <see cref="Destinations"/>
    /// instead.
    /// </summary>
    public GitTarget Github { get; set; } = new();

    /// <summary>S17a SHIM for the "drive" destination - see <see cref="Github"/>'s doc comment for the full reasoning; identical shape, just for the well-known "drive" id.</summary>
    public DriveTarget Drive { get; set; } = new();

    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>
    /// The real, N-destination, on-disk model (S17a). Every entry's <see
    /// cref="BackupDestination.Id"/> is unique. A file at
    /// BackupConfigVersion 2 or later trusts this list as-is; a stale file
    /// has it rebuilt from <see cref="Github"/>/<see cref="Drive"/> by <see
    /// cref="Load"/>'s migration (see the version-2 migration step) with the
    /// well-known ids "github"/"drive". Multiple destinations of the same
    /// <see cref="DestinationKind"/> are explicitly allowed - nothing here
    /// enforces one-per-kind.
    /// </summary>
    public List<BackupDestination> Destinations { get; set; } = new();

    /// <summary>
    /// Schema version. 0 (the type default) means "written before
    /// per-destination selection existed" (Include/Exclude lived directly on
    /// BackupConfig, shared by both destinations). 1 means "per-destination
    /// selection exists (Github.Include/Drive.Include), but the destination
    /// list does not yet - still exactly Github + Drive". Bumped to <see
    /// cref="CurrentBackupConfigVersion"/> (2: the <see cref="Destinations"/>
    /// list) by <see cref="Load"/> once a stale file has been migrated
    /// through whichever of the two steps above it needs. Modeled on
    /// AppSettings' NotificationStateVersion.
    /// </summary>
    public int BackupConfigVersion { get; set; }

    /// <summary>
    /// Current schema version. S17a: bumped from 1 to 2 for the
    /// Github/Drive -&gt; <see cref="Destinations"/> list migration. Bump
    /// again when the destination shape changes again.
    /// </summary>
    public const int CurrentBackupConfigVersion = 2;

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
        // Destinations is the real on-disk shape - see Github/Drive's own
        // doc comments - so a fresh config needs it populated too, not just
        // the two shim fields, otherwise Default().Save() would round-trip
        // through an empty destination list.
        result.SyncDestinationsFromLegacyShims();
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
            config.Migrate(path);
            // Destinations - not Github/Drive - is authoritative on read (see
            // Github's own doc comment): whatever the "github"/"drive"
            // entries say wins, even if a hand-edited file's top-level
            // "Github"/"Drive" JSON had since drifted from them.
            config.SyncLegacyShimsFromDestinations();
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
        // MigrateLegacySelectionStep already guard. An out-of-range Transport
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

        // S17a: the same defensive shape, extended to Destinations - a
        // hand-edited or future-version backup.json can carry a null list
        // entry, a null nested list, or an out-of-range Kind, and none of
        // that may reach BackupHealth/BackupStatusWriter/a future
        // BackupRunner as anything other than a well-formed value.
        Destinations ??= new();
        Destinations.RemoveAll(d => d is null);
        foreach (var d in Destinations)
        {
            d.Id ??= "";
            d.Name ??= "";
            d.RemoteUrl ??= "";
            d.Branch ??= "main";
            d.FolderPath ??= "";
            d.RcloneRemote ??= "";
            d.Include ??= new();
            d.Exclude ??= new();
            if (!Enum.IsDefined(typeof(DestinationKind), d.Kind))
                d.Kind = DestinationKind.Rclone;
            if (!Enum.IsDefined(typeof(SyncProvider), d.SyncProvider))
                d.SyncProvider = SyncProvider.Other;
        }
    }

    /// <summary>
    /// Runs whichever migration steps a stale file needs, in order (a
    /// version-0 file must pass through both to reach the current shape),
    /// and persists once at the end if anything actually changed - mirrors
    /// the existing single-save-per-Load pattern rather than writing the
    /// file out twice for a doubly-stale config.
    /// </summary>
    private void Migrate(string path)
    {
        // A JSON payload with an explicit null for any of these (rather than
        // the field simply being absent) would otherwise NRE below - see
        // AppSettings.NotificationState's "??= new()" for the same
        // defensive pattern after a real bug from exactly this shape.
        Include ??= new();
        Exclude ??= new();
        Github ??= new();
        Drive ??= new();
        Destinations ??= new();

        if (BackupConfigVersion >= CurrentBackupConfigVersion)
            return;

        MigrateLegacySelectionStep();
        MigrateToDestinationListStep();

        BackupConfigVersion = CurrentBackupConfigVersion;
        Save(path);
    }

    /// <summary>
    /// One-time migration from the pre-per-destination shape: a backup.json
    /// at BackupConfigVersion 0 with a non-empty legacy Include/Exclude had
    /// that ONE list shared by both destinations. Copying it onto BOTH
    /// Github and Drive here preserves exactly what an existing user's
    /// backup used to select - nothing they configured changes meaning, on
    /// purpose; this is not the place to "improve" on it.
    ///
    /// Gated on the version being EXACTLY 0 (not just stale), and on the
    /// legacy lists actually being non-empty: a file at version 1 (or later)
    /// has already been through this step in an earlier Load() and must not
    /// repeat it even though it is still stale relative to
    /// CurrentBackupConfigVersion (S17a added a second step below); a file
    /// at version 0 with empty legacy lists (a user who cleared both boxes
    /// before this feature existed) has nothing to copy, and
    /// Github.Include/Drive.Include already deserialize to empty lists on
    /// their own (the type's field initializer), which is the exact same end
    /// state copying empty lists onto them would produce - so skipping the
    /// copy in that case changes nothing observable.
    /// </summary>
    private void MigrateLegacySelectionStep()
    {
        Github.Include ??= new();
        Github.Exclude ??= new();
        Drive.Include ??= new();
        Drive.Exclude ??= new();
        Drive.FolderPath ??= ""; // S14: same defensive guard as Include/Exclude above, for an explicit JSON null

        if (BackupConfigVersion != 0)
            return;

        if (Include.Count > 0 || Exclude.Count > 0)
        {
            Github.Include = new List<string>(Include);
            Github.Exclude = new List<string>(Exclude);
            Drive.Include = new List<string>(Include);
            Drive.Exclude = new List<string>(Exclude);
            Include.Clear();
            Exclude.Clear();
        }
    }

    /// <summary>
    /// S17a's own migration step: folds the two positional destinations
    /// (Github, Drive - by this point already carrying whatever
    /// MigrateLegacySelectionStep produced, if anything) into <see
    /// cref="Destinations"/>, under the WELL-KNOWN ids "github" and "drive".
    /// This is what makes the backup-status.json migration deterministic
    /// (see BackupStatus.Load's own migration) - a status entry and its
    /// owning destination agree on the id without any matching-by-name
    /// heuristic. Guarded on Destinations being empty so this never clobbers
    /// real N-destination data with a rebuild from the (possibly stale)
    /// Github/Drive shim - in practice this only ever runs for a file at
    /// BackupConfigVersion 0 or 1, neither of which had a "Destinations" JSON
    /// array to begin with, so the guard is defensive rather than load-bearing.
    /// </summary>
    private void MigrateToDestinationListStep()
    {
        if (Destinations.Count > 0)
            return;

        Destinations = new List<BackupDestination>
        {
            FromGitHubShim("github", Github),
            FromDriveShim("drive", Drive),
        };
    }

    public void Save(string path)
    {
        // Github/Drive are a SHIM (see their own doc comments) - Destinations
        // is what actually gets serialized as the durable shape, so whatever
        // a not-yet-migrated caller (BackupRunner/SettingsForm/RestoreDialog)
        // just mutated on the shim must be folded back in before writing,
        // or those edits would silently vanish the next time this file is
        // loaded.
        SyncDestinationsFromLegacyShims();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Pushes the current Github/Drive shim values (see <see
    /// cref="Github"/>'s doc comment) into Destinations' "github"/"drive"
    /// entries, creating either entry if it is not present yet. Any OTHER
    /// destination already in the list (S17c's future "add destination" UI,
    /// or a test constructing N destinations directly) is left completely
    /// untouched - this only ever writes the two well-known ids.
    /// </summary>
    private void SyncDestinationsFromLegacyShims()
    {
        UpsertDestination(FromGitHubShim("github", Github));
        UpsertDestination(FromDriveShim("drive", Drive));
    }

    private void UpsertDestination(BackupDestination replacement)
    {
        var index = Destinations.FindIndex(d => d.Id == replacement.Id);
        if (index >= 0)
            Destinations[index] = replacement;
        else
            Destinations.Add(replacement);
    }

    /// <summary>
    /// Pulls the well-known "github"/"drive" entries (if present) back out
    /// of Destinations into the Github/Drive shim - the read-side half of
    /// the sync <see cref="Save"/> performs on write; see <see
    /// cref="Github"/>'s doc comment for why this shim exists at all. A
    /// destination list with no "github" (or "drive") id - not possible yet
    /// from anything in this codebase, since nothing before S17c can remove
    /// a destination, but not assumed here either - leaves the corresponding
    /// shim at its type default (Enabled: false), which is the correct "not
    /// configured" reading for BackupRunner/SettingsForm/RestoreDialog.
    /// </summary>
    private void SyncLegacyShimsFromDestinations()
    {
        var github = Destinations.FirstOrDefault(d => d.Id == "github");
        Github = github is null ? new GitTarget() : ToGitHubShim(github);

        var drive = Destinations.FirstOrDefault(d => d.Id == "drive");
        Drive = drive is null ? new DriveTarget() : ToDriveShim(drive);
    }

    private static BackupDestination FromGitHubShim(string id, GitTarget git) => new()
    {
        Id = id,
        Name = "GitHub",
        Kind = DestinationKind.GitHub,
        Enabled = git.Enabled,
        RemoteUrl = git.RemoteUrl,
        Branch = git.Branch,
        Include = new List<string>(git.Include),
        Exclude = new List<string>(git.Exclude),
    };

    private static BackupDestination FromDriveShim(string id, DriveTarget drive) => new()
    {
        Id = id,
        // Reuses BackupHealth's own transport-aware naming (S14/S16) rather
        // than a second, possibly-drifting copy of the same switch - see
        // BackupHealth.DriveDisplayName's doc comment.
        Name = BackupHealth.DriveDisplayName(drive),
        Kind = drive.Transport == DriveTransport.SyncFolder ? DestinationKind.SyncFolder : DestinationKind.Rclone,
        Enabled = drive.Enabled,
        FolderPath = drive.FolderPath,
        SyncProvider = drive.SyncProvider,
        RcloneRemote = drive.RcloneRemote,
        Include = new List<string>(drive.Include),
        Exclude = new List<string>(drive.Exclude),
        KeepLastCount = drive.KeepLastCount,
        DeleteOlderThanDays = drive.DeleteOlderThanDays,
    };

    private static GitTarget ToGitHubShim(BackupDestination d) => new()
    {
        Enabled = d.Enabled,
        RemoteUrl = d.RemoteUrl,
        Branch = string.IsNullOrEmpty(d.Branch) ? "main" : d.Branch,
        Include = new List<string>(d.Include),
        Exclude = new List<string>(d.Exclude),
    };

    private static DriveTarget ToDriveShim(BackupDestination d) => new()
    {
        Enabled = d.Enabled,
        Transport = d.Kind == DestinationKind.SyncFolder ? DriveTransport.SyncFolder : DriveTransport.Rclone,
        FolderPath = d.FolderPath,
        SyncProvider = d.SyncProvider,
        RcloneRemote = d.RcloneRemote,
        Include = new List<string>(d.Include),
        Exclude = new List<string>(d.Exclude),
        KeepLastCount = d.KeepLastCount,
        DeleteOlderThanDays = d.DeleteOlderThanDays,
    };
}

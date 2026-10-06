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
/// Which mechanism a zip-based destination (<see cref="DriveTarget"/>, and -
/// via <see cref="BackupDestination.ToDriveTarget"/> - a <see
/// cref="DestinationKind.SyncFolder"/>/<see cref="DestinationKind.Rclone"/>
/// <see cref="BackupDestination"/>) uploads through (S14 design doc:
/// "sync-folder backup transport"). <see cref="Rclone"/> = 0 is the default
/// specifically so a backup.json written before this field existed loads
/// with Transport already correct - a property absent from old JSON
/// deserializing to its type default is exactly the intended back-compat
/// behaviour, which is also why this change does not bump <see
/// cref="BackupConfig.CurrentBackupConfigVersion"/>.
///
/// S17a/S17b/S17c: superseded as the on-disk model by <see
/// cref="DestinationKind"/> on <see cref="BackupDestination"/>, which
/// distinguishes GitHub as a real third kind rather than folding it into
/// this same switch - this enum survives only as the legacy <see
/// cref="DriveTarget"/> shape's own field (still used as an adapter type by
/// the backends/restore engine - see <see
/// cref="BackupDestination.ToDriveTarget"/>) and as raw-JSON migration input
/// for a pre-v2 backup.json (see <c>BackupConfig.MigrateFromLegacyJson</c>).
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

    /// <summary>
    /// S17b: adapts this destination into the <see cref="GitTarget"/> shape
    /// <see cref="ClaudeBackup.GitBackend"/>/<see cref="ClaudeBackup.RestoreGitSource"/>
    /// still take as their own parameter type (out of this task's scope to
    /// change) - meaningful only when <see cref="Kind"/> is <see
    /// cref="DestinationKind.GitHub"/>, but callers do not need to check that
    /// themselves; a non-GitHub destination just produces a GitTarget whose
    /// fields nobody downstream reads. BackupRunner and RestoreDialog both use
    /// this instead of hand-rolling the same field-by-field copy twice.
    /// </summary>
    public GitTarget ToGitTarget() => new()
    {
        Enabled = Enabled,
        RemoteUrl = RemoteUrl,
        Branch = Branch,
        Include = Include,
        Exclude = Exclude,
    };

    /// <summary>
    /// S17b: adapts this destination into the <see cref="DriveTarget"/> shape
    /// <see cref="ClaudeBackup.RcloneBackend"/>/<see cref="ClaudeBackup.SyncFolderBackend"/>/
    /// the restore engine still take - see <see cref="ToGitTarget"/>'s doc
    /// comment for the same reasoning, applied to the two zip-based kinds
    /// instead of GitHub. <see cref="DriveTarget.Transport"/> is derived from
    /// <see cref="Kind"/> exactly like <see cref="BackupConfig"/>'s own
    /// migration already does for the "drive" shim entry.
    /// </summary>
    public DriveTarget ToDriveTarget() => new()
    {
        Enabled = Enabled,
        Transport = Kind == DestinationKind.SyncFolder ? DriveTransport.SyncFolder : DriveTransport.Rclone,
        FolderPath = FolderPath,
        SyncProvider = SyncProvider,
        RcloneRemote = RcloneRemote,
        Include = Include,
        Exclude = Exclude,
        KeepLastCount = KeepLastCount,
        DeleteOlderThanDays = DeleteOlderThanDays,
    };
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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
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

        // S17c: Destinations is the ONLY on-disk shape now (the Github/Drive
        // shim is gone - see this class's own doc comment history) - a fresh
        // config is built directly against the well-known "github"/"drive"
        // ids. Both destinations get the SAME defaults today - a brand new
        // user has never configured anything to diverge, and giving each its
        // own independent copy of the same lists is what makes editing just
        // one of them later (via the destinations dialog) a real, isolated
        // change instead of secretly touching both.
        var result = new BackupConfig
        {
            // A freshly created config is already at the current shape, so a
            // later Load() of what this writes never mistakes it for a
            // pre-migration file (mirrors SettingsStore's fresh.Normalize()
            // stamping NotificationStateVersion before a fresh AppSettings'
            // first save, for the identical reason).
            BackupConfigVersion = CurrentBackupConfigVersion,
            Destinations = new List<BackupDestination>
            {
                new()
                {
                    Id = "github", Name = "GitHub", Kind = DestinationKind.GitHub,
                    Include = new List<string>(include), Exclude = new List<string>(exclude),
                },
                new()
                {
                    Id = "drive", Name = BackupHealth.DriveDisplayName(new DriveTarget()), Kind = DestinationKind.Rclone,
                    Include = new List<string>(include), Exclude = new List<string>(exclude),
                },
            },
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
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<BackupConfig>(json) ?? Default();
            config.Migrate(path, json);
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
    /// Runs whichever migration steps a stale file needs, and persists once
    /// at the end if anything actually changed - mirrors the existing
    /// single-save-per-Load pattern rather than writing the file out twice
    /// for a doubly-stale config.
    ///
    /// S17c: the old <c>Github</c>/<c>Drive</c> shim properties are gone
    /// (SettingsForm now reads/writes <see cref="Destinations"/> directly -
    /// see this class's own history), so migration can no longer lean on
    /// ordinary property deserialization to recover a legacy file's
    /// top-level "Github"/"Drive" JSON objects - <paramref name="json"/> is
    /// re-parsed here instead, mirroring exactly how <see
    /// cref="ClaudeBackup.BackupStatus.Load"/> already recovers ITS OWN
    /// legacy top-level "Github"/"Drive" keys via a raw <see
    /// cref="JsonDocument"/> pass (see that method's own remarks - this is
    /// the identical problem, solved the identical way, one class over).
    /// </summary>
    private void Migrate(string path, string json)
    {
        Destinations ??= new();
        Include ??= new();
        Exclude ??= new();

        if (BackupConfigVersion >= CurrentBackupConfigVersion)
            return;

        MigrateFromLegacyJson(json);

        BackupConfigVersion = CurrentBackupConfigVersion;
        Save(path);
    }

    /// <summary>
    /// Recovers a pre-v2 file's legacy "Github"/"Drive" top-level JSON
    /// objects (deserializing each into the same <see cref="GitTarget"/>/
    /// <see cref="DriveTarget"/> shapes the adapters elsewhere in this file
    /// already use - not new types, since those two classes already have
    /// exactly the right fields) and folds them into <see
    /// cref="Destinations"/> under the WELL-KNOWN ids "github"/"drive". This
    /// is what makes the backup-status.json migration deterministic (see
    /// BackupStatus.Load's own migration) - a status entry and its owning
    /// destination agree on the id without any matching-by-name heuristic.
    ///
    /// Also folds in the even-older v0 shape (a single top-level
    /// Include/Exclude shared by both destinations, gated on
    /// BackupConfigVersion being EXACTLY 0 and the legacy lists actually
    /// being non-empty - see the pre-S17c version of this method for the
    /// same gating, now expressed against the raw JSON instead of a
    /// deserialized property) before building the two destinations, so a v0
    /// file passes through both steps in one call, exactly as it always has.
    ///
    /// Guarded on Destinations being empty so this never clobbers real
    /// N-destination data - in practice this only ever runs for a file at
    /// BackupConfigVersion 0 or 1, neither of which had a "Destinations"
    /// JSON array to begin with, so the guard is defensive rather than
    /// load-bearing.
    /// </summary>
    private void MigrateFromLegacyJson(string json)
    {
        if (Destinations.Count > 0)
            return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var github = ReadLegacyObject<GitTarget>(root, "Github");
        var drive = ReadLegacyObject<DriveTarget>(root, "Drive");

        if (BackupConfigVersion == 0)
        {
            var legacyInclude = ReadStringArray(root, "Include");
            var legacyExclude = ReadStringArray(root, "Exclude");
            if (legacyInclude.Count > 0 || legacyExclude.Count > 0)
            {
                github.Include = new List<string>(legacyInclude);
                github.Exclude = new List<string>(legacyExclude);
                drive.Include = new List<string>(legacyInclude);
                drive.Exclude = new List<string>(legacyExclude);
                Include.Clear();
                Exclude.Clear();
            }
        }

        Destinations = new List<BackupDestination>
        {
            DestinationFromLegacyGitHub("github", github),
            DestinationFromLegacyDrive("drive", drive),
        };
    }

    /// <summary>Deserializes <paramref name="propertyName"/>'s raw JSON object into a fresh <typeparamref name="T"/>, or a fresh default when the property is absent or explicitly null.</summary>
    private static T ReadLegacyObject<T>(JsonElement root, string propertyName) where T : new() =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind != JsonValueKind.Null
            ? JsonSerializer.Deserialize<T>(element.GetRawText()) ?? new T()
            : new T();

    /// <summary>Reads a top-level JSON string array property, tolerating an absent property, a non-array value, or non-string elements (skipped) - never throws.</summary>
    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in element.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                result.Add(s);
        return result;
    }

    private static BackupDestination DestinationFromLegacyGitHub(string id, GitTarget git) => new()
    {
        Id = id,
        Name = "GitHub",
        Kind = DestinationKind.GitHub,
        Enabled = git.Enabled,
        RemoteUrl = git.RemoteUrl,
        Branch = string.IsNullOrEmpty(git.Branch) ? "main" : git.Branch,
        Include = new List<string>(git.Include),
        Exclude = new List<string>(git.Exclude),
    };

    private static BackupDestination DestinationFromLegacyDrive(string id, DriveTarget drive) => new()
    {
        Id = id,
        // Reuses BackupHealth's own transport-aware naming (S14/S16) rather
        // than a second, possibly-drifting copy of the same switch - see
        // BackupHealth.DriveDisplayName's doc comment.
        Name = BackupHealth.DriveDisplayName(drive),
        Kind = drive.Transport == DriveTransport.SyncFolder ? DestinationKind.SyncFolder : DestinationKind.Rclone,
        Enabled = drive.Enabled,
        FolderPath = drive.FolderPath ?? "",
        SyncProvider = drive.SyncProvider,
        RcloneRemote = drive.RcloneRemote,
        Include = new List<string>(drive.Include),
        Exclude = new List<string>(drive.Exclude),
        KeepLastCount = drive.KeepLastCount,
        DeleteOlderThanDays = drive.DeleteOlderThanDays,
    };

    /// <summary>
    /// S17c: writes only <see cref="Destinations"/> (and every other real
    /// property) - the old "Github"/"Drive" shim is gone, so a v2 config no
    /// longer carries the duplicate legacy top-level JSON keys the S17a/S17b
    /// design notes flagged as inert-but-present duplication. A pre-v2 file
    /// still round-trips correctly: <see cref="Load"/>'s migration reads the
    /// legacy keys once, off the raw JSON text, before this method is ever
    /// reached for that file (see <see cref="MigrateFromLegacyJson"/>).
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

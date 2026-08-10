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

public sealed class DriveTarget
{
    public bool Enabled { get; set; }
    public string RcloneRemote { get; set; } = "";

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
        return new BackupConfig
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
    }

    public static BackupConfig Load(string path)
    {
        if (!File.Exists(path))
            return Default();
        try
        {
            var config = JsonSerializer.Deserialize<BackupConfig>(File.ReadAllText(path)) ?? Default();
            config.MigrateLegacySelection(path);
            return config;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return Default();
        }
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

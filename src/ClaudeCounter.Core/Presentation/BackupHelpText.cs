namespace ClaudeCounter.UI;

/// <summary>
/// Pure, testable home for the Backup tab's help copy. Both the per-field
/// info popups (shown by BackupDestinationEditDialog) and the "Help" guide
/// dialog read from here rather than embedding strings directly in a Form,
/// so the copy can be unit tested without constructing one (this project's
/// rule against constructing a Form in a test - see
/// SettingsFormValidationTests).
///
/// S17c: rewritten for any-number-of-destinations-of-any-kind (the "GitHub
/// plus one cloud/NAS" restriction this text used to describe was never a
/// real constraint of the model - only of the old UI - and is gone). The
/// facts here are load-bearing: they were checked against the shipped code
/// (BackupConfig.Default(), the secret denylist, BackupTaskManager) rather
/// than invented, per the "Help content" section of
/// docs/superpowers/specs/2026-08-10-settings-redesign.md. Do not edit an
/// entry without checking that document first - getting one of these facts
/// wrong would mislead a user about where their credentials go.
/// </summary>
public static class BackupHelpText
{
    public const string Name =
        "The name shown for this destination everywhere in ClaudeCounter - " +
        "the destinations list, the Backup tab's summary, health, and " +
        "Restore's \"Restore from\" list. Freely editable; purely a label, " +
        "never used to look anything up.";

    public const string GithubEnabled =
        "Copies a subset of ~/.claude to a Git repository you control. It is " +
        "a plain file copy - it never touches your Claude account or usage. " +
        "Requires a repository you create yourself; see Help for setup steps.";

    public const string RemoteUrl =
        "The Git remote to push backups to. Create a NEW, EMPTY, PRIVATE " +
        "repository first, then paste its URL - the SSH form " +
        "(git@github.com:you/repo.git) is simplest. Authentication uses your " +
        "existing Git Credential Manager; ClaudeCounter never handles, " +
        "stores or logs a token, and a URL with an embedded token is " +
        "rejected.";

    public const string Branch =
        "The branch backups are pushed to (e.g. main). Created automatically " +
        "on the remote if it does not already exist.";

    public const string DestinationEnabled =
        "Turns this destination on. A disabled destination is skipped on " +
        "every run and never shows a health warning - use this to pause a " +
        "destination temporarily without losing its settings, or leave a " +
        "newly-added destination off until you have finished configuring it.";

    public const string SyncFolder =
        "No sign-in or API setup needed. Point this at a folder your sync " +
        "client already watches - Google Drive for Desktop, OneDrive, " +
        "Dropbox all present as ordinary folders, and the sync client does " +
        "the actual upload on its own. A NAS share works the same way. " +
        "Picking Google Drive, OneDrive, Dropbox or NAS / network share " +
        "when adding a destination already tries to find and fill this in " +
        "for you; use Detect... to search again, or Browse... to pick one " +
        "by hand. For a NAS, prefer a UNC path (\\\\server\\share\\...) over " +
        "a mapped drive letter (M:\\...) - a drive letter mapping is tied " +
        "to your interactive sign-in session and is not guaranteed to " +
        "resolve when the backup runs from Task Scheduler, so a " +
        "letter-based path can silently fail once scheduled while a UNC " +
        "path keeps working; Detect... already offers the UNC form for a " +
        "mapped drive for this reason.";

    public const string RcloneRemote =
        "The rclone remote and destination folder, e.g. gdrive:ClaudeBackups. " +
        "Must name a remote you already created with 'rclone config'. If you " +
        "used the recommended drive.file scope, let rclone create this " +
        "folder on first upload rather than making it yourself in Drive.";

    public const string Include =
        "Glob patterns (one per line), relative to ~/.claude, for what gets " +
        "copied - e.g. commands/**. Every destination has its own Include " +
        "list, independent of every other destination's. Files that are " +
        "never backed up (credentials, keys, tokens - see Help) are skipped " +
        "even if a pattern here would otherwise match them.";

    public const string Exclude =
        "Glob patterns (one per line), relative to ~/.claude, for what to " +
        "skip, checked after Include. Every destination has its own Exclude " +
        "list, independent of every other destination's. Files that are " +
        "never backed up (credentials, keys, tokens - see Help) are always " +
        "skipped regardless of this list.";

    public const string Retention =
        "Keep only the most recent N backups, and/or delete backups older " +
        "than N days - when both are ticked, a backup is pruned if EITHER " +
        "rule would remove it, and the single most recent backup is never " +
        "deleted no matter what these settings say. Applies only to " +
        "sync-folder and rclone destinations, which accumulate one zip per " +
        "run - a GitHub destination keeps its full history in git commits " +
        "instead, so retention does not apply to it.";

    /// <summary>
    /// Every field that has an info popup, keyed by a short identifier.
    /// BackupDestinationEditDialog wires each field's button to its own
    /// constant directly; this dictionary exists so a test can walk "every
    /// field with a popup" without duplicating the field list.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FieldTopics =
        new Dictionary<string, string>
        {
            ["Name"] = Name,
            ["GithubEnabled"] = GithubEnabled,
            ["RemoteUrl"] = RemoteUrl,
            ["Branch"] = Branch,
            ["DestinationEnabled"] = DestinationEnabled,
            ["SyncFolder"] = SyncFolder,
            ["RcloneRemote"] = RcloneRemote,
            ["Include"] = Include,
            ["Exclude"] = Exclude,
            ["Retention"] = Retention,
        };

    public const string FullGuide =
        "WHAT BACKUP DOES\r\n" +
        "Copies a chosen subset of your Claude configuration (~/.claude) to " +
        "any number of destinations you configure, of any kind, in any " +
        "combination. It never touches your Claude account or usage; it is " +
        "a file copy.\r\n" +
        "\r\n" +
        "DESTINATIONS\r\n" +
        "Settings -> Backup -> \"Manage destinations...\" lets you add, " +
        "edit, or remove destinations one at a time: GitHub, Google Drive, " +
        "OneDrive, Dropbox, NAS / network share, or an rclone remote " +
        "(advanced). You can set up any of them, all of them, or several of " +
        "the same kind - two GitHub repos, or a home NAS and an office NAS, " +
        "both work, each with its own settings. Each destination has its " +
        "own connection details, its own Include/Exclude selection, and (for " +
        "the sync-folder and rclone kinds) its own retention settings. " +
        "Removing a destination only removes it from ClaudeCounter's local " +
        "configuration - it does not delete anything at the destination " +
        "itself, and its health/notification entry is dropped along with " +
        "it.\r\n" +
        "\r\n" +
        "GITHUB\r\n" +
        "WHAT IT NEEDS: a NEW, EMPTY, PRIVATE repository you create " +
        "yourself. The repository must be private, and ClaudeCounter cannot " +
        "verify that for you.\r\n" +
        "AUTHENTICATION: your existing Git Credential Manager, or an SSH " +
        "key for the SSH remote form (git@github.com:you/repo.git, the " +
        "simplest option). ClaudeCounter never handles, stores, or logs a " +
        "token, and a Remote URL with an embedded credential is rejected " +
        "outright.\r\n" +
        "WHAT'S BACKED UP: whatever this destination's own Include/Exclude " +
        "selects (see \"what is backed up by default\" below).\r\n" +
        "VERSIONING / RETENTION: every run is a commit on the configured " +
        "branch - git history IS the version history, so retention settings " +
        "do not apply to a GitHub destination; there is nothing to prune.\r\n" +
        "RESTORE: lists commits from the branch and lets you preview and " +
        "apply the files from any of them.\r\n" +
        "\r\n" +
        "SYNC FOLDER (recommended) - Google Drive, OneDrive, Dropbox, NAS / " +
        "network share\r\n" +
        "WHAT THEY NEED: a folder your sync client (Google Drive for " +
        "Desktop, OneDrive, Dropbox) or NAS share already watches - no " +
        "sign-in, no API setup. Picking one of these kinds when adding a " +
        "destination already tries to find and fill in the folder for you; " +
        "use Detect... to search again (e.g. after signing into a sync " +
        "client that was not set up yet when the destination was added), or " +
        "Browse... to pick one by hand.\r\n" +
        "AUTHENTICATION: none needed at all - the sync client (or the NAS " +
        "itself) does the actual upload on its own; ClaudeCounter never " +
        "signs in to anything for these.\r\n" +
        "WHAT'S BACKED UP: whatever this destination's own Include/Exclude " +
        "selects, zipped into a single timestamped archive each run.\r\n" +
        "VERSIONING / RETENTION: this app's own retention settings (see " +
        "below) - the sync client does not keep old versions of the zip on " +
        "its own. For a NAS, prefer a UNC path (\\\\server\\share\\...) over " +
        "a mapped drive letter (M:\\...) - a drive letter mapping belongs to " +
        "your interactive sign-in session and is not guaranteed to resolve " +
        "when the backup runs from Task Scheduler, so a letter-based path " +
        "can silently fail once scheduled while a UNC path keeps working; " +
        "Detect... already offers the UNC form for a mapped drive for this " +
        "reason.\r\n" +
        "RESTORE: lists the timestamped zips found in the folder and lets " +
        "you preview and apply the files from any of them.\r\n" +
        "\r\n" +
        "ADVANCED: RCLONE REMOTE\r\n" +
        "The original method, still available for a destination a sync " +
        "folder cannot reach directly (any of the 70+ services rclone " +
        "supports). Install rclone and authorise it once with 'rclone " +
        "config'.\r\n" +
        "WHAT IT NEEDS, for Google Drive specifically: YOUR OWN Google " +
        "OAuth client ID - rclone's shared client is being retired and will " +
        "stop working during 2026, and is heavily rate limited meanwhile. " +
        "Create your own in the Google Cloud Console.\r\n" +
        "\r\n" +
        "While creating it, PUBLISH the OAuth app rather than leaving it in " +
        "Testing. An app left in Testing issues refresh tokens that expire " +
        "after 7 days, so a scheduled backup works for a week and then " +
        "starts failing silently - the worst failure mode for a backup you " +
        "are not watching. Publishing does not require Google's " +
        "verification for personal use under 100 users; you will just see " +
        "an 'unverified app' warning at sign-in, which is safe to click " +
        "through.\r\n" +
        "\r\n" +
        "When rclone asks for a scope, choose option 3, drive.file, rather " +
        "than full drive access - rclone then only ever touches files it " +
        "created itself, so a compromised client cannot reach the rest of " +
        "your Drive. The catch: rclone cannot see folders it did not " +
        "create, so do not pre-create the backup folder in the Drive web " +
        "UI - let the first upload create it.\r\n" +
        "\r\n" +
        "AUTHENTICATION: whatever 'rclone config' set up for the remote - " +
        "ClaudeCounter only ever invokes the rclone binary with the remote " +
        "name you give it, it never sees or stores a credential itself.\r\n" +
        "WHAT'S BACKED UP: whatever this destination's own Include/Exclude " +
        "selects, zipped and uploaded each run - give the remote as " +
        "remote:folder, e.g. gdrive:ClaudeBackups.\r\n" +
        "VERSIONING / RETENTION: this app's own retention settings (see " +
        "below), same as the sync-folder kind.\r\n" +
        "RESTORE: lists the timestamped zips found on the remote and lets " +
        "you preview and apply the files from any of them.\r\n" +
        "\r\n" +
        "Full walkthrough, sync folder first and the rclone appendix after " +
        "it: docs/GOOGLE-DRIVE-SETUP.md in the repo.\r\n" +
        "\r\n" +
        "WHAT IS BACKED UP BY DEFAULT\r\n" +
        "settings.json, CLAUDE.md, commands/, agents/, and top-level " +
        "plugins/*.json - the same defaults for a newly-added destination " +
        "until you change it.\r\n" +
        "\r\n" +
        "WHAT IS EXCLUDED BY DEFAULT\r\n" +
        "projects/ (session history, large and regenerable), statsig/, and " +
        "any cache/ directory.\r\n" +
        "\r\n" +
        "EVERY DESTINATION HAS ITS OWN SELECTION\r\n" +
        "Include and Exclude are independent per destination, edited on that " +
        "destination's own page in \"Manage destinations...\". A private git " +
        "repo and a NAS zip can reasonably hold different things; changing " +
        "one destination's selection never touches another's.\r\n" +
        "\r\n" +
        "WHAT IS NEVER BACKED UP, WHATEVER YOU CONFIGURE\r\n" +
        ".credentials.json (live OAuth tokens), session.dat (this app's " +
        "encrypted session), *.key, *.pem, *.pfx and similar, .env, " +
        ".npmrc, .git-credentials, SSH private keys, and any file whose " +
        "name contains token, secret, credential or apikey. This list is " +
        "enforced in code and cannot be overridden from this dialog or by " +
        "editing backup.json. Files dropped this way are named in the log " +
        "so a legitimate file caught by the name rules is visible rather " +
        "than silently missing.\r\n" +
        "\r\n" +
        "NOTE ON THE DEEP PLUGIN TREE\r\n" +
        "plugins/** is deliberately NOT backed up by default. Plugin and " +
        "MCP config files commonly embed inline API keys in " +
        "innocuously-named files that a name-based filter cannot catch. " +
        "Add it knowingly if you want it.\r\n" +
        "\r\n" +
        "SCHEDULING\r\n" +
        "'Save and register schedule' creates a per-user Windows Task " +
        "Scheduler task named 'ClaudeCounter Backup' that runs every " +
        "enabled destination each time it fires. Disabling every " +
        "destination (or removing them all) and saving removes it. " +
        "Uninstalling removes it too.\r\n" +
        "\r\n" +
        "ADVANCED SCHEDULE SETTINGS\r\n" +
        "The Advanced dialog controls what happens around a scheduled run: " +
        "whether a missed backup (e.g. the machine was asleep) runs as soon " +
        "as possible afterwards (on by default), whether the task requires " +
        "a network connection to start (on by default, since every " +
        "destination uploads somewhere), whether it is allowed to start or " +
        "must stop on battery power (both off by default - a laptop is " +
        "exactly where missed runs happen, so backups run on battery unless " +
        "you turn these on), and whether a failed run retries automatically " +
        "(on by default, every 15 minutes, up to 3 times).\r\n" +
        "\r\n" +
        "BACKUP RETENTION\r\n" +
        "Each sync-folder or rclone destination has its own retention " +
        "section (in \"Manage destinations...\", editing that destination) " +
        "that can keep it from accumulating backups forever: keep only the " +
        "most recent N, delete anything older than N days, or both - both " +
        "off by default. When both are on, a backup is pruned if either " +
        "rule would remove it. Pruning only ever considers this app's own " +
        "claude-backup-*.zip files, only runs after a successful upload (a " +
        "failed run never prunes anything), and never deletes the single " +
        "most recent backup no matter how the settings are set. GitHub has " +
        "no equivalent - each run is a commit, and git history is the " +
        "retention model.\r\n" +
        "\r\n" +
        "RESULTS\r\n" +
        "'Back up now' reports one of three outcomes - complete, 'not run, " +
        "check your settings' (nothing enabled, nothing selected on ANY " +
        "enabled destination, or a bad setting), or failed (a destination " +
        "failed - network, auth, disk, or a git/rclone error). One " +
        "destination with an empty selection does not stop another enabled " +
        "destination that does have files - that destination still runs, " +
        "and the empty one is skipped and logged. Details always go to the " +
        "log.";
}

namespace ClaudeCounter.UI;

/// <summary>
/// Pure, testable home for the Backup tab's help copy. Both the per-field
/// info popups and the "Help" guide dialog read from here rather than
/// embedding strings directly in SettingsForm, so the copy can be unit
/// tested without constructing a Form (this project's rule against
/// constructing a Form in a test - see SettingsFormValidationTests).
///
/// The facts here are load-bearing: they were checked against the shipped
/// code (BackupConfig.Default(), the secret denylist, BackupTaskManager)
/// rather than invented, per the "Help content" section of
/// docs/superpowers/specs/2026-08-10-settings-redesign.md. Do not edit an
/// entry without checking that document first - getting one of these facts
/// wrong would mislead a user about where their credentials go.
/// </summary>
public static class BackupHelpText
{
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

    public const string DriveEnabled =
        "Copies a timestamped zip to the Drive destination, using whichever " +
        "transport is selected below - a sync folder (no sign-in needed) or " +
        "rclone (advanced). See Help for setup steps for either.";

    public const string SyncFolder =
        "No sign-in or API setup needed. Point this at a folder your sync " +
        "client already watches - Google Drive for Desktop, OneDrive, " +
        "Dropbox all present as ordinary folders, and the sync client does " +
        "the actual upload on its own. A NAS share works the same way. Use " +
        "Detect... to find one automatically, or Browse... to pick one by " +
        "hand. For a NAS, prefer a UNC path (\\\\server\\share\\...) over a " +
        "mapped drive letter (M:\\...) - a drive letter mapping is tied to " +
        "your interactive sign-in session and is not guaranteed to resolve " +
        "when the backup runs from Task Scheduler, so a letter-based path " +
        "can silently fail once scheduled while a UNC path keeps working; " +
        "Detect... already offers the UNC form for a mapped drive for this " +
        "reason.";

    public const string RcloneRemote =
        "The rclone remote and destination folder, e.g. gdrive:ClaudeBackups. " +
        "Must name a remote you already created with 'rclone config'. If you " +
        "used the recommended drive.file scope, let rclone create this " +
        "folder on first upload rather than making it yourself in Drive.";

    public const string Include =
        "Glob patterns (one per line), relative to ~/.claude, for what gets " +
        "copied - e.g. commands/**. GitHub and Drive each have their " +
        "own Include list - use the selector above to switch which one you " +
        "are editing. Files that are never backed up (credentials, keys, " +
        "tokens - see Help) are skipped even if a pattern here would " +
        "otherwise match them.";

    public const string Exclude =
        "Glob patterns (one per line), relative to ~/.claude, for what to " +
        "skip, checked after Include. GitHub and Drive each have " +
        "their own Exclude list - use the selector above to switch which " +
        "one you are editing. Files that are never backed up (credentials, " +
        "keys, tokens - see Help) are always skipped regardless of this " +
        "list.";

    /// <summary>
    /// Every field that has an info popup, keyed by a short identifier.
    /// SettingsForm wires each field's button to its own constant directly;
    /// this dictionary exists so a test can walk "every field with a popup"
    /// without duplicating the field list.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FieldTopics =
        new Dictionary<string, string>
        {
            ["GithubEnabled"] = GithubEnabled,
            ["RemoteUrl"] = RemoteUrl,
            ["Branch"] = Branch,
            ["DriveEnabled"] = DriveEnabled,
            ["SyncFolder"] = SyncFolder,
            ["RcloneRemote"] = RcloneRemote,
            ["Include"] = Include,
            ["Exclude"] = Exclude,
        };

    public const string FullGuide =
        "WHAT BACKUP DOES\r\n" +
        "Copies a chosen subset of your Claude configuration (~/.claude) to " +
        "a destination you control. It never touches your Claude account or " +
        "usage; it is a file copy.\r\n" +
        "\r\n" +
        "GITHUB SETUP\r\n" +
        "Create a NEW, EMPTY, PRIVATE repository first. Paste its URL - SSH " +
        "(git@github.com:you/repo.git) is simplest. Authentication uses " +
        "your existing Git Credential Manager; ClaudeCounter never handles, " +
        "stores, or logs a token, and a URL with an embedded token is " +
        "rejected. The repository must be private, and ClaudeCounter " +
        "cannot verify that for you.\r\n" +
        "\r\n" +
        "DRIVE SETUP\r\n" +
        "The Drive destination has two transports - pick one with the " +
        "Transport dropdown. They are NOT combined: the Drive destination " +
        "uses one transport at a time, so you cannot back up to both a NAS " +
        "(sync folder) and an rclone remote at once. GitHub plus one of " +
        "them still works fine, since GitHub is a separate destination.\r\n" +
        "\r\n" +
        "SYNC FOLDER (recommended) - no sign-in, no API setup\r\n" +
        "Point it at a folder your sync client already watches - Google " +
        "Drive for Desktop, OneDrive, and Dropbox all present as ordinary " +
        "folders, and the sync client does the actual upload on its own; a " +
        "NAS share works the same way. Click Detect... to find one " +
        "automatically, or Browse... to pick one by hand. For a NAS, " +
        "prefer a UNC path (\\\\server\\share\\...) over a mapped drive " +
        "letter (M:\\...) - a drive letter mapping belongs to your " +
        "interactive sign-in session and is not guaranteed to resolve when " +
        "the backup runs from Task Scheduler, so a letter-based path can " +
        "silently fail once scheduled while a UNC path keeps working; " +
        "Detect... already offers the UNC form for a mapped drive for this " +
        "reason. Each run copies a timestamped zip straight into the " +
        "folder.\r\n" +
        "\r\n" +
        "ADVANCED: RCLONE REMOTE\r\n" +
        "The original method, still available for a destination sync " +
        "folder cannot reach directly. Install rclone and authorise it " +
        "once with 'rclone config'. You need YOUR OWN Google OAuth client " +
        "ID for this - rclone's shared client is being retired and will " +
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
        "Then give the remote as remote:folder, e.g. gdrive:ClaudeBackups. " +
        "Each run uploads a timestamped zip.\r\n" +
        "\r\n" +
        "Full walkthrough, sync folder first and the rclone appendix after " +
        "it: docs/GOOGLE-DRIVE-SETUP.md in the repo.\r\n" +
        "\r\n" +
        "WHAT IS BACKED UP BY DEFAULT\r\n" +
        "settings.json, CLAUDE.md, commands/, agents/, and top-level " +
        "plugins/*.json - the same defaults for both destinations until you " +
        "change one of them.\r\n" +
        "\r\n" +
        "WHAT IS EXCLUDED BY DEFAULT\r\n" +
        "projects/ (session history, large and regenerable), statsig/, and " +
        "any cache/ directory.\r\n" +
        "\r\n" +
        "GITHUB AND DRIVE HAVE SEPARATE SELECTIONS\r\n" +
        "Include and Exclude are independent per destination - the selector " +
        "above the boxes picks which one you are editing. A private git " +
        "repo and a Drive zip can reasonably hold different things; " +
        "changing GitHub's selection does not touch Drive's, or vice " +
        "versa.\r\n" +
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
        "Scheduler task named 'ClaudeCounter Backup'. Disabling both " +
        "destinations and saving removes it. Uninstalling removes it too.\r\n" +
        "\r\n" +
        "ADVANCED SCHEDULE SETTINGS\r\n" +
        "The Advanced dialog controls what happens around a scheduled run: " +
        "whether a missed backup (e.g. the machine was asleep) runs as soon " +
        "as possible afterwards (on by default), whether the task requires " +
        "a network connection to start (on by default, since both " +
        "destinations upload), whether it is allowed to start or must stop " +
        "on battery power (both off by default - a laptop is exactly where " +
        "missed runs happen, so backups run on battery unless you turn " +
        "these on), and whether a failed run retries automatically (on by " +
        "default, every 15 minutes, up to 3 times).\r\n" +
        "\r\n" +
        "DRIVE RETENTION\r\n" +
        "The same Advanced dialog can keep Drive from accumulating backups " +
        "forever - the same rules apply to both transports, sync folder or " +
        "rclone: keep only the most recent N, delete anything older than " +
        "N days, or both - both off by default. When both are on, a backup " +
        "is pruned if either rule would remove it. Pruning only ever " +
        "considers this app's own claude-backup-*.zip files, only runs " +
        "after a successful upload (a failed run never prunes anything), " +
        "and never deletes the single most recent backup no matter how the " +
        "settings are set. GitHub has no equivalent - each run is a commit, " +
        "and git history is the retention model.\r\n" +
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

using System.Text.Json;

namespace ClaudeBackup;

/// <summary>Outcome of one destination's most recent backup attempt.</summary>
public enum BackupOutcome { Success, Failed }

/// <summary>
/// Per-destination slice of <see cref="BackupStatus"/>. All four rules from
/// the design spec's "Rules for writing it" are enforced by <see
/// cref="WithAttempt"/>, the only way this type's fields should ever change:
/// <list type="number">
/// <item>Written on every terminating path - see BackupRunner.RunDetailed
/// and Program.RunWorker, which produce a <see cref="DestinationAttempt"/>
/// for every return path, including config errors and the offender abort.</item>
/// <item>Never contains a credential - <see cref="WithAttempt"/> scrubs the
/// message through <see cref="CredentialScrubber"/> before storing it.</item>
/// <item>A failed status write never fails the run - enforced by <see
/// cref="BackupStatusWriter"/>, not here.</item>
/// <item>Last success is never overwritten by a failure - <see
/// cref="WithAttempt"/> only advances <see cref="LastSuccessUtc"/> on a
/// successful attempt; a failing attempt updates <see cref="LastAttemptUtc"/>
/// and <see cref="LastOutcome"/> but leaves <see cref="LastSuccessUtc"/>
/// untouched. This is the property BackupHealth's staleness computation
/// depends on.</item>
/// </list>
/// </summary>
public sealed class DestinationStatus
{
    public DateTimeOffset? LastAttemptUtc { get; set; }

    /// <summary>
    /// Distinct from <see cref="LastAttemptUtc"/> - the basis for staleness.
    /// Only ever moves forward, on a successful attempt (rule 4 above).
    /// </summary>
    public DateTimeOffset? LastSuccessUtc { get; set; }

    /// <summary>Null only when no attempt has ever been recorded (BackupHealth reads this as NeverRun).</summary>
    public BackupOutcome? LastOutcome { get; set; }

    /// <summary>Always scrubbed - see <see cref="WithAttempt"/>.</summary>
    public string LastMessage { get; set; } = "";

    /// <summary>Whether this destination was enabled in the config as of the most recent update.</summary>
    public bool WasEnabled { get; set; }

    /// <summary>
    /// Returns a new <see cref="DestinationStatus"/> reflecting one run's
    /// outcome for this destination. When <paramref name="attempt"/> was not
    /// actually attempted this run (e.g. the destination is currently
    /// disabled, or the run aborted before reaching it), every field except
    /// <see cref="WasEnabled"/> is carried forward unchanged - a run that
    /// never touched a destination must not overwrite what that destination's
    /// last real attempt said.
    /// </summary>
    public DestinationStatus WithAttempt(DestinationAttempt attempt, DateTimeOffset now)
    {
        if (!attempt.Attempted)
        {
            return new DestinationStatus
            {
                LastAttemptUtc = LastAttemptUtc,
                LastSuccessUtc = LastSuccessUtc,
                LastOutcome = LastOutcome,
                LastMessage = LastMessage,
                WasEnabled = attempt.Enabled,
            };
        }

        // Scrubbed here - the one choke point every stored message passes
        // through, exactly as backend output is already scrubbed before
        // logging (CredentialScrubber's own doc comment) - rclone and git
        // error output can echo a remote spec, and messages arriving here
        // came from BackupRunner/Program, both of which already scrub before
        // handing a message to BackupStatusWriter. Scrubbing again here is
        // deliberately redundant: it is what makes "never contains a
        // credential" a property of the status file itself, not merely of
        // however many call sites currently remember to scrub first.
        var scrubbed = CredentialScrubber.Scrub(attempt.Message);
        return new DestinationStatus
        {
            LastAttemptUtc = now,
            LastSuccessUtc = attempt.Success ? now : LastSuccessUtc,
            LastOutcome = attempt.Success ? BackupOutcome.Success : BackupOutcome.Failed,
            LastMessage = scrubbed,
            WasEnabled = attempt.Enabled,
        };
    }
}

/// <summary>
/// Persisted record of the most recent backup run(s), one <see
/// cref="DestinationStatus"/> per destination plus the overall exit code and
/// run time. Written by the worker (ClaudeBackup.exe) via <see
/// cref="BackupStatusWriter"/>, read by the tray (a later task) to compute
/// backup health - see <see cref="BackupHealth"/>. Lives in
/// ClaudeCounter.Shared, not ClaudeBackup.csproj, so the tray never needs a
/// ProjectReference on the worker (same reasoning as BackupConfig,
/// CredentialScrubber, IProcessRunner - see their doc comments; that
/// reference is what breaks the tray's single-file publish).
/// </summary>
public sealed class BackupStatus
{
    public DestinationStatus Github { get; set; } = new();
    public DestinationStatus Drive { get; set; } = new();

    /// <summary>The most recent run's overall exit code (0/1/2 - see BackupRunner's doc comment).</summary>
    public int? LastExitCode { get; set; }

    /// <summary>When the most recent run happened, regardless of outcome.</summary>
    public DateTimeOffset? LastRunUtc { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCounter", "backup-status.json");

    /// <summary>
    /// Loads the status file at <paramref name="path"/>. A missing, corrupt,
    /// or partially-null file degrades to a fresh <see cref="BackupStatus"/>
    /// (which BackupHealth reads as NeverRun for every destination) rather
    /// than throwing - this is read on every worker run before writing, and
    /// a status file with no history to lose must never crash a backup that
    /// would otherwise have succeeded.
    /// </summary>
    public static BackupStatus Load(string path)
    {
        if (!File.Exists(path))
            return new BackupStatus();
        try
        {
            var status = JsonSerializer.Deserialize<BackupStatus>(File.ReadAllText(path), Options) ?? new BackupStatus();
            // A JSON payload with an explicit null for either (rather than
            // the field simply being absent) would otherwise NRE the first
            // time WithAttempt is called on it - see BackupConfig's
            // MigrateLegacySelection for the same defensive pattern after a
            // real bug from exactly this shape.
            status.Github ??= new();
            status.Drive ??= new();
            return status;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new BackupStatus();
        }
    }

    /// <summary>
    /// Writes this status to <paramref name="path"/> via a temp-file-then-move,
    /// matching BackupConfig.Save's atomicity. Can throw (a locked or
    /// ACL-denied path) - callers that must never let a write failure affect
    /// the run's own outcome go through <see cref="BackupStatusWriter"/>
    /// instead of calling this directly.
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Returns a new <see cref="BackupStatus"/> reflecting one run's result:
    /// each destination via <see cref="DestinationStatus.WithAttempt"/> (which
    /// is what actually enforces "last success is never overwritten by a
    /// failure"), and <see cref="LastExitCode"/>/<see cref="LastRunUtc"/>
    /// unconditionally advanced to this run's values - unlike the
    /// per-destination fields, there is no "carry forward" case for these
    /// two: every terminating path has an exit code and a time, even one that
    /// touched no destination at all (e.g. "no backup destinations enabled").
    /// </summary>
    public BackupStatus WithRun(BackupRunResult result, DateTimeOffset now) => new()
    {
        Github = Github.WithAttempt(result.Github, now),
        Drive = Drive.WithAttempt(result.Drive, now),
        LastExitCode = result.ExitCode,
        LastRunUtc = now,
    };
}

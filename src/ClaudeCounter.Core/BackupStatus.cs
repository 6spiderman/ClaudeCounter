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
/// cref="DestinationStatus"/> per destination (S17a: keyed by <see
/// cref="BackupDestination.Id"/>, over an arbitrary number of destinations -
/// see <see cref="Destinations"/>) plus the overall exit code and run time.
/// Written by the worker (ClaudeBackup.exe) via <see
/// cref="BackupStatusWriter"/>, read by the tray to compute backup health -
/// see <see cref="BackupHealth"/>. Lives in ClaudeCounter.Core, not
/// ClaudeBackup.csproj, so the tray never needs a ProjectReference on the
/// worker (same reasoning as BackupConfig, CredentialScrubber,
/// IProcessRunner - see their doc comments; that reference is what breaks
/// the tray's single-file publish).
/// </summary>
public sealed class BackupStatus
{
    /// <summary>
    /// Per-destination status, keyed by <see cref="BackupDestination.Id"/>.
    /// S17a: replaces the old literal top-level "Github"/"Drive" JSON
    /// properties - see <see cref="Load"/>'s migration for how a file
    /// written before this existed is folded into this shape, using the same
    /// well-known "github"/"drive" ids BackupConfig's own migration assigns
    /// to the same two destinations. An id present here with no matching
    /// destination in the current BackupConfig (the destination was removed)
    /// is an ORPHAN - BackupHealth.Evaluate already ignores it (it only ever
    /// looks up ids that exist in BackupConfig.Destinations), and <see
    /// cref="PruneOrphaned"/>/<see cref="RemoveDestination"/> exist to
    /// reclaim the entry itself once a destination is actually removed
    /// (S17c).
    /// </summary>
    public Dictionary<string, DestinationStatus> Destinations { get; set; } = new();

    /// <summary>The most recent run's overall exit code (0/1/2 - see BackupRunner's doc comment).</summary>
    public int? LastExitCode { get; set; }

    /// <summary>When the most recent run happened, regardless of outcome.</summary>
    public DateTimeOffset? LastRunUtc { get; set; }

    /// <summary>
    /// Schema version - added by S17a. There was no version field at all
    /// before this (see <see cref="Load"/>'s remarks on why that made the
    /// migration harder than BackupConfig's own): every file written before
    /// this property existed deserializes it as 0 (the type default, since
    /// the property is simply absent from that JSON) AND stored each
    /// destination directly under literal top-level "Github"/"Drive" keys
    /// instead of <see cref="Destinations"/>. 0 is therefore read as "maybe
    /// the legacy shape" by <see cref="Load"/>, which is the only place that
    /// can actually tell a genuinely-fresh status apart from a stale one (by
    /// looking for those literal keys in the raw JSON).
    /// </summary>
    public int BackupStatusVersion { get; set; }

    /// <summary>Current schema version - bump when the per-destination status shape changes again.</summary>
    public const int CurrentBackupStatusVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "ClaudeCounter", "backup-status.json");

    /// <summary>
    /// Returns this destination's status, or a fresh (NeverRun-reads-as)
    /// <see cref="DestinationStatus"/> when <paramref name="destinationId"/>
    /// has no entry - the same "never seen" reading BackupHealth.Evaluate
    /// already gives an orphaned or brand-new id.
    /// </summary>
    public DestinationStatus For(string destinationId) =>
        Destinations.TryGetValue(destinationId, out var status) ? status : new DestinationStatus();

    /// <summary>
    /// Loads the status file at <paramref name="path"/>. A missing, corrupt,
    /// ACL-denied, or partially-null file degrades to a fresh <see
    /// cref="BackupStatus"/> (which BackupHealth reads as NeverRun for every
    /// destination) rather than throwing. This is read on every worker run
    /// before writing (a status file with no history to lose must never
    /// crash a backup that would otherwise have succeeded), AND - unlike
    /// BackupConfig.Load, whose only production caller sits inside another
    /// method's blanket try/catch - the tray is expected to call this
    /// directly on a poll timer with no surrounding try of its own, so a
    /// throw here would take the tray down. Degrading, not throwing, is the
    /// whole contract.
    ///
    /// S17a's legacy-shape migration (see <see cref="MigrateLegacyShape"/>)
    /// is deliberately IN-MEMORY ONLY - it does not save, unlike
    /// BackupConfig.Load's migration. This method is called on every tray
    /// poll and must stay a cheap, side-effect-free read; the file only
    /// actually gets rewritten in the new shape the next time
    /// BackupStatusWriter.Record performs a real write (Load, then WithRun,
    /// then Save) - until then, a legacy file is simply re-migrated in
    /// memory on every read, which is correct (if slightly repeated) work,
    /// not a bug.
    /// </summary>
    public static BackupStatus Load(string path)
    {
        if (!File.Exists(path))
            return new BackupStatus();
        try
        {
            var json = File.ReadAllText(path);
            var status = JsonSerializer.Deserialize<BackupStatus>(json, Options) ?? new BackupStatus();
            status.Destinations ??= new();
            // A JSON payload with an explicit null for a destination's value
            // (rather than the key simply being absent) would otherwise NRE
            // the first time WithAttempt is called on it - see
            // BackupConfig.Normalize for the same defensive pattern after a
            // real bug from exactly this shape.
            foreach (var key in status.Destinations.Keys.ToList())
                status.Destinations[key] ??= new DestinationStatus();

            if (status.BackupStatusVersion < CurrentBackupStatusVersion)
            {
                // A stale version alone does not mean "legacy shape" - a
                // status this codebase itself already wrote in the new
                // Destinations-keyed shape (e.g. via Save() directly, without
                // going through WithRun, which is the only place that stamps
                // BackupStatusVersion) would ALSO read as version 0 here, and
                // must not have its correctly-deserialized Destinations
                // clobbered by a migration that finds no legacy keys to
                // recover anything from. Only actually migrate when the raw
                // JSON has the literal "Github"/"Drive" top-level keys this
                // migration exists to read; otherwise just stamp the version
                // forward - there is nothing to fold in.
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Github", out _) || doc.RootElement.TryGetProperty("Drive", out _))
                    status.MigrateLegacyShape(doc.RootElement);
                status.BackupStatusVersion = CurrentBackupStatusVersion;
            }

            return status;
        }
        // Fix round 1 (Important 2): widened from JsonException/IOException,
        // matching BackupConfig.Load's identical widening and for the same
        // reason - UnauthorizedAccessException does not derive from
        // IOException, and NotSupportedException can come from
        // JsonSerializer for a shape it cannot handle. Both must degrade to
        // a fresh BackupStatus here exactly like a corrupt file does.
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new BackupStatus();
        }
    }

    /// <summary>
    /// S17a: a status file written before <see cref="Destinations"/> existed
    /// has no version field at all and stores each destination directly as a
    /// top-level "Github"/"Drive" JSON object - properties this class no
    /// longer declares, so the ordinary JsonSerializer.Deserialize call in
    /// <see cref="Load"/> silently skipped them (an unmapped JSON property is
    /// ignored by default, not an error) and <see cref="Destinations"/> came
    /// back empty. This recovers those two objects straight from the raw
    /// JSON text and maps them onto the same well-known "github"/"drive" ids
    /// BackupConfig's own v1-&gt;v2 migration uses for the same two
    /// destinations - which is exactly what makes this migration
    /// deterministic: a status entry and its owning destination agree on the
    /// id without any matching-by-name heuristic.
    ///
    /// Always populates BOTH ids (with a fresh, NeverRun-reading
    /// DestinationStatus when the corresponding JSON key is absent OR an
    /// explicit null) - mirroring the pre-S17a guarantee that
    /// status.Github/status.Drive were never null, just represented here as
    /// "the dictionary always has both well-known entries after a legacy
    /// file is migrated" instead.
    /// </summary>
    private void MigrateLegacyShape(JsonElement root)
    {
        Destinations["github"] = ReadLegacyDestinationStatus(root, "Github");
        Destinations["drive"] = ReadLegacyDestinationStatus(root, "Drive");
    }

    private static DestinationStatus ReadLegacyDestinationStatus(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind != JsonValueKind.Null)
            return JsonSerializer.Deserialize<DestinationStatus>(element.GetRawText()) ?? new DestinationStatus();
        return new DestinationStatus();
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
    /// each attempted destination via <see cref="DestinationStatus.WithAttempt"/>
    /// (which is what actually enforces "last success is never overwritten by
    /// a failure"), merged over whatever this status already had (S17a: N
    /// destinations, not two positional ones - an id in <paramref
    /// name="result"/> with no prior entry starts from a fresh
    /// DestinationStatus; an id already present but NOT mentioned in this
    /// run's <see cref="BackupRunResult.Attempts"/> is left completely
    /// untouched, which is the N-destination equivalent of the old "carry
    /// forward unchanged" case). <see cref="LastExitCode"/>/<see
    /// cref="LastRunUtc"/> are unconditionally advanced to this run's values
    /// - unlike the per-destination entries, there is no "carry forward" case
    /// for these two: every terminating path has an exit code and a time,
    /// even one that touched no destination at all (e.g. "no backup
    /// destinations enabled").
    /// </summary>
    public BackupStatus WithRun(BackupRunResult result, DateTimeOffset now)
    {
        var next = new Dictionary<string, DestinationStatus>(Destinations);
        foreach (var (id, attempt) in result.Attempts)
        {
            var previous = next.TryGetValue(id, out var existing) ? existing : new DestinationStatus();
            next[id] = previous.WithAttempt(attempt, now);
        }

        return new BackupStatus
        {
            Destinations = next,
            LastExitCode = result.ExitCode,
            LastRunUtc = now,
            BackupStatusVersion = CurrentBackupStatusVersion,
        };
    }

    /// <summary>
    /// Removal semantics (S17a, wired up by S17c's "remove destination" UI):
    /// drops one destination's status entirely, so BackupHealth stops
    /// reporting something that no longer exists in BackupConfig. A no-op
    /// (returns this same instance) when <paramref name="destinationId"/>
    /// has no entry.
    /// </summary>
    public BackupStatus WithoutDestination(string destinationId)
    {
        if (!Destinations.ContainsKey(destinationId))
            return this;

        var next = new Dictionary<string, DestinationStatus>(Destinations);
        next.Remove(destinationId);
        return new BackupStatus { Destinations = next, LastExitCode = LastExitCode, LastRunUtc = LastRunUtc, BackupStatusVersion = BackupStatusVersion };
    }

    /// <summary>
    /// Bulk form of <see cref="WithoutDestination"/>: keeps only entries
    /// whose id is in <paramref name="validDestinationIds"/>, dropping every
    /// ORPHAN (an id with no matching destination in the current
    /// BackupConfig). BackupHealth.Evaluate already ignores an orphan on its
    /// own - it only ever looks up ids that exist in
    /// BackupConfig.Destinations - so this exists purely to reclaim the file
    /// itself, not to change what health reports. Not called automatically
    /// from anywhere in this task (a normal run's BackupRunResult does not
    /// yet cover every configured destination - BackupRunner is still
    /// two-destination-only until S17b - so pruning against "every id this
    /// run mentioned" would incorrectly delete a real, still-configured
    /// destination's history); intended for callers that know the FULL
    /// current set of valid ids independently, such as S17c's "remove
    /// destination" flow.
    /// </summary>
    public BackupStatus PruneOrphaned(IEnumerable<string> validDestinationIds)
    {
        var keep = new HashSet<string>(validDestinationIds);
        var next = Destinations.Where(kv => keep.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return new BackupStatus { Destinations = next, LastExitCode = LastExitCode, LastRunUtc = LastRunUtc, BackupStatusVersion = BackupStatusVersion };
    }

    /// <summary>
    /// Convenience wrapper around <see cref="WithoutDestination"/> for S17c's
    /// "remove destination" UI: load, drop one destination's status, save -
    /// in one call, at the file this codebase actually reads/writes from.
    /// Deliberately NOT routed through BackupStatusWriter's swallow-on-failure
    /// pattern: that pattern exists so a status-write problem can never fail
    /// a BACKUP RUN (rule 3), which does not apply here - removing a
    /// destination is a user-initiated settings action, not a run, so a
    /// write failure is the caller's (S17c's) to handle, not something to
    /// silently swallow.
    /// </summary>
    public static void RemoveDestination(string path, string destinationId)
    {
        var status = Load(path);
        status.WithoutDestination(destinationId).Save(path);
    }
}

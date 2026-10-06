using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.Core;

/// <summary>
/// What the running tray app publishes after every poll, in
/// <c>usage-status.json</c>, for <c>claudecounter --status</c> and the Plasma
/// widget to read. They only ever read this file: they never authenticate or
/// call the usage API themselves, because a second process refreshing
/// ClaudeCounter's own session could rotate its token out from under the app.
/// Holds usage numbers and the poll state only - never a token or the account
/// email.
/// </summary>
public sealed record UsageStatusFile(
    int Version,
    string AppVersion,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSuccessAt,
    int PollIntervalMinutes,
    string Problem,
    string? ProblemMessage,
    string Source,
    int WarnThreshold,
    int CriticalThreshold,
    UsageSnapshot? Snapshot)
{
    public const int CurrentVersion = 1;

    /// <summary>Builds the file contents from the poll state the trays already have.</summary>
    public static UsageStatusFile From(PollState state, int pollIntervalMinutes, int warnThreshold, int criticalThreshold, DateTimeOffset now) => new(
        CurrentVersion,
        AppInfo.Version,
        now,
        state.LastSuccessAt,
        pollIntervalMinutes,
        ProblemName(state.Problem),
        state.ProblemMessage,
        SourceName(state.Source),
        warnThreshold,
        criticalThreshold,
        state.Snapshot);

    public static string ProblemName(ProblemKind problem) => problem switch
    {
        ProblemKind.SignInRequired => "signInRequired",
        ProblemKind.TokenExpired => "tokenExpired",
        ProblemKind.RateLimited => "rateLimited",
        ProblemKind.Network => "network",
        _ => "none",
    };

    public static string SourceName(TokenSource? source) => source switch
    {
        TokenSource.OwnSession => "own",
        TokenSource.CliBootstrap => "claudeCode",
        TokenSource.EnvironmentVariable => "environment",
        _ => "none",
    };
}

/// <summary>Reads and writes <see cref="UsageStatusFile"/>.</summary>
public static class UsageStatusStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Next to backup-status.json: %LocalAppData%\ClaudeCounter or ~/.local/share/ClaudeCounter.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "ClaudeCounter", "usage-status.json");

    /// <summary>
    /// Writes atomically (temp file, then move) so a reader never sees half a
    /// file; owner-only on Linux. Best-effort: a failure is logged, never thrown -
    /// the tray must keep working if the status file cannot be written.
    /// </summary>
    public static void Write(UsageStatusFile status, string? path = null)
    {
        path ??= DefaultPath();
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(status, Json));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not write the usage status file: {e.Message}");
        }
    }

    /// <summary>Null when the file is missing or unreadable.</summary>
    public static UsageStatusFile? Read(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            if (!File.Exists(path))
                return null;
            var status = JsonSerializer.Deserialize<UsageStatusFile>(File.ReadAllText(path), Json);
            return status is { Version: UsageStatusFile.CurrentVersion } ? status : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>One usage window as <c>--status --json</c> reports it.</summary>
public sealed record UsageStatusWindow(
    string Key,
    string Label,
    double Utilization,
    DateTimeOffset? ResetsAt,
    string? ResetsIn,
    string Band);

/// <summary>
/// What <c>claudecounter --status</c> reports: the published file plus what
/// can only be known when reading it - whether the app is still running,
/// whether the numbers are stale, the band colours, and live countdowns.
/// </summary>
public sealed record UsageStatusReport(
    bool Running,
    bool Stale,
    bool Available,
    string Line,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? LastSuccessAt,
    string Problem,
    string? ProblemMessage,
    string Source,
    IReadOnlyList<UsageStatusWindow> Windows,
    ExtraUsage? ExtraUsage,
    string? AppVersion)
{
    private static readonly (string Key, string Label, Func<UsageSnapshot, UsageWindow?> Get)[] WindowOrder =
    [
        ("fiveHour", "5-hour session", s => s.FiveHour),
        ("sevenDay", "Weekly (all models)", s => s.SevenDay),
        ("sevenDayOpus", "Weekly (Opus)", s => s.SevenDayOpus),
        ("sevenDaySonnet", "Weekly (Sonnet)", s => s.SevenDaySonnet),
    ];

    /// <summary>
    /// The published numbers count as stale once they are older than two
    /// poll intervals (at least 15 minutes), or the app that keeps them
    /// current is no longer running.
    /// </summary>
    public static bool IsStale(UsageStatusFile file, bool running, DateTimeOffset now)
    {
        if (!running)
            return true;
        var maxAge = TimeSpan.FromMinutes(Math.Max(15, 2 * Math.Max(1, file.PollIntervalMinutes)));
        return now - file.UpdatedAt > maxAge;
    }

    /// <summary>Builds the report. Pure: <paramref name="running"/> comes from the caller.</summary>
    public static UsageStatusReport From(UsageStatusFile? file, bool running, DateTimeOffset now)
    {
        if (file is null)
        {
            var line = running ? "ClaudeCounter: no usage yet" : "ClaudeCounter: not running";
            return new UsageStatusReport(running, true, false, line, null, null, "none", null, "none", [], null, null);
        }

        var stale = IsStale(file, running, now);
        var gray = stale || file.Problem != "none";
        var windows = new List<UsageStatusWindow>();
        if (file.Snapshot is { } snapshot)
        {
            foreach (var (key, label, get) in WindowOrder)
            {
                if (get(snapshot) is not { } window)
                    continue;
                var band = gray
                    ? UsageBand.Gray
                    : UsageBands.For(window.Utilization, file.WarnThreshold, file.CriticalThreshold);
                windows.Add(new UsageStatusWindow(
                    key, label, window.Utilization, window.ResetsAt,
                    window.ResetsAt is { } at ? TimeText.Countdown(at, now) : null,
                    UsageBands.Name(band)));
            }
        }

        var available = windows.Count > 0;
        return new UsageStatusReport(
            running, stale, available,
            FormatLine(windows, file, running, stale),
            file.UpdatedAt, file.LastSuccessAt, file.Problem, file.ProblemMessage, file.Source,
            windows, file.Snapshot?.ExtraUsage, file.AppVersion);
    }

    /// <summary>
    /// The one-line text, for a status line or prompt:
    /// <c>5h 42% (1 h 12 min) | week 25% (2 d 19 h)</c>, with <c>| stale</c>
    /// or <c>| not running</c> appended when the numbers may be out of date.
    /// ASCII only, so it survives any terminal or code page.
    /// </summary>
    private static string FormatLine(IReadOnlyList<UsageStatusWindow> windows, UsageStatusFile file, bool running, bool stale)
    {
        if (windows.Count == 0)
        {
            return file.Problem switch
            {
                "signInRequired" or "tokenExpired" => "ClaudeCounter: not signed in",
                _ when !running => "ClaudeCounter: not running",
                _ => "ClaudeCounter: no usage yet",
            };
        }

        var parts = new List<string>();
        foreach (var w in windows.Where(w => w.Key is "fiveHour" or "sevenDay"))
        {
            var name = w.Key == "fiveHour" ? "5h" : "week";
            parts.Add(w.ResetsIn is { } resetsIn
                ? $"{name} {w.Utilization:0}% ({resetsIn})"
                : $"{name} {w.Utilization:0}%");
        }
        if (parts.Count == 0) // only per-model windows - show the first of them
            parts.Add($"{windows[0].Label} {windows[0].Utilization:0}%");

        if (!running)
            parts.Add("not running");
        else if (stale)
            parts.Add("stale");
        return string.Join(" | ", parts);
    }

    public string ToJson() => JsonSerializer.Serialize(this, UsageStatusStore.Json);
}

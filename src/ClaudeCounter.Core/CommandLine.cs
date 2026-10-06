namespace ClaudeCounter.Core;

/// <summary>
/// The command-line options both apps handle before anything else - before the
/// single-instance mutex, so they work while the tray app is running, and
/// without starting any UI or touching the network.
/// </summary>
/// <remarks>
/// <c>--status</c> only reads the file the tray app publishes
/// (<see cref="UsageStatusStore"/>); <c>--refresh</c> only asks the running app
/// to poll, over <see cref="InstanceChannel"/>. Neither ever authenticates.
/// </remarks>
public static class CommandLine
{
    public const int Ok = 0;

    /// <summary>Nothing to show: the app is not running, not signed in, or has no usage yet.</summary>
    public const int Unavailable = 1;

    public const int UsageError = 2;

    public const string HelpText =
        """
        Usage: claudecounter [option]

        With no option, starts ClaudeCounter in the system tray (or, if it is
        already running, opens its usage window).

          --status          Print current usage on one line, e.g.
                              5h 42% (1 h 12 min) | week 25% (2 d 19 h)
          --status --json   Print current usage as JSON.
          --refresh         Ask the running ClaudeCounter to check usage now.
          --version         Print the version.
          --help            Print this help.

        --status reads what the running ClaudeCounter last published; it never
        signs in or contacts Claude itself. Exit code 0 means usage was printed,
        1 means there was nothing to show (not running, not signed in, or no
        usage yet), 2 means the options were not understood.
        """;

    /// <summary>What the commands need from the outside world, so tests can fake it.</summary>
    public sealed record Environment(
        Func<bool> IsAppRunning,
        Func<UsageStatusFile?> ReadStatus,
        Func<bool> SendRefresh,
        Func<DateTimeOffset> Now)
    {
        public static Environment Real { get; } = new(
            () => InstanceChannel.TrySend(InstanceChannel.Ping, TimeSpan.FromMilliseconds(250)),
            () => UsageStatusStore.Read(),
            () => InstanceChannel.TrySend(InstanceChannel.Refresh, TimeSpan.FromSeconds(2)),
            () => DateTimeOffset.Now);
    }

    /// <summary>True when the app should handle <paramref name="args"/> as a command and exit, rather than start.</summary>
    public static bool IsCommand(string[] args) => args.Length > 0;

    /// <summary>
    /// Runs the command in <paramref name="args"/> and returns the exit code.
    /// Call only when <see cref="IsCommand"/> is true.
    /// </summary>
    public static int Run(string[] args, TextWriter output, TextWriter error, Environment env)
    {
        var options = new HashSet<string>(args, StringComparer.Ordinal);
        var unknown = options.Where(a => a is not ("--status" or "--json" or "--refresh" or "--version" or "--help" or "-h" or "-?" or "/?")).ToList();
        if (unknown.Count > 0)
        {
            error.WriteLine($"claudecounter: unknown option '{unknown[0]}'");
            error.WriteLine("Run 'claudecounter --help' for the options.");
            return UsageError;
        }

        if (options.Overlaps(["--help", "-h", "-?", "/?"]))
        {
            output.WriteLine(HelpText);
            return Ok;
        }
        if (options.Contains("--version"))
        {
            output.WriteLine($"ClaudeCounter {AppInfo.Version}");
            return Ok;
        }
        if (options.Contains("--refresh"))
        {
            if (options.Count > 1)
                return Misuse(error, "--refresh takes no other options.");
            if (env.SendRefresh())
                return Ok;
            error.WriteLine("ClaudeCounter: not running");
            return Unavailable;
        }
        if (options.Contains("--status"))
        {
            var report = UsageStatusReport.From(env.ReadStatus(), env.IsAppRunning(), env.Now());
            output.WriteLine(options.Contains("--json") ? report.ToJson() : report.Line);
            return report.Available ? Ok : Unavailable;
        }
        return Misuse(error, "--json only goes with --status.");
    }

    private static int Misuse(TextWriter error, string message)
    {
        error.WriteLine($"claudecounter: {message}");
        error.WriteLine("Run 'claudecounter --help' for the options.");
        return UsageError;
    }
}

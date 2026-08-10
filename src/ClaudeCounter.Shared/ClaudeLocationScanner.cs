namespace ClaudeBackup;

/// <summary>
/// One of the known candidate locations for Claude's local config/state, after
/// an existence check - see <see cref="ClaudeLocationScanner"/>. Only ever
/// constructed for a path that was found to exist; there is no "missing"
/// instance of this type.
/// </summary>
public sealed record ClaudeLocation(string DisplayName, string Path, bool IsDirectory);

/// <summary>File count and total byte size for one location or tree node, as measured by <see cref="ClaudeLocationScanner.ComputeStats"/>.</summary>
public readonly record struct DirectoryStats(int FileCount, long TotalBytes);

/// <summary>
/// Probes a fixed list of known Claude config/state locations and reports which
/// ones actually exist on this machine - see the task-1 report and the design
/// spec's "Measured reality" table for what was found on the reference
/// machine. Never throws: a candidate that cannot be probed (e.g. a denied
/// parent directory) is simply treated as absent rather than aborting the
/// whole probe.
///
/// Deliberately two-phase, mirroring the file picker's own lazy/off-thread
/// sizing: <see cref="Probe()"/> only checks existence (File.Exists /
/// Directory.Exists - no enumeration), so opening the Backup tab or the
/// picker dialog never walks a real ~/.claude (7,837 files on the reference
/// machine) just to find out what is there. Counting and sizing a location is
/// a separate call (<see cref="ComputeStats"/>) that a caller makes lazily,
/// off the UI thread, only for a location the user is actually looking at.
/// </summary>
public static class ClaudeLocationScanner
{
    /// <summary>
    /// Probes the real, fixed set of known locations on this machine (built
    /// from %USERPROFILE%, %APPDATA%, %LOCALAPPDATA%) and returns the ones
    /// that exist.
    /// </summary>
    public static IReadOnlyList<ClaudeLocation> Probe() => ProbeCandidates(KnownCandidates());

    /// <summary>
    /// The fixed candidate list this feature was designed against (see the
    /// design spec's "Measured reality" table): ~/.claude and ~/.claude.json
    /// exist on the reference machine; %LOCALAPPDATA%\Claude exists there
    /// too but nearly empty; %APPDATA%\Claude, %LOCALAPPDATA%\AnthropicClaude
    /// and ~/.config/claude were checked and do not exist there, so they are
    /// probed - never assumed - rather than omitted, in case a different
    /// machine or a future Claude Desktop version puts something there.
    /// </summary>
    internal static IReadOnlyList<ClaudeLocation> KnownCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new List<ClaudeLocation>
        {
            new("Claude config (~/.claude)", System.IO.Path.Combine(home, ".claude"), true),
            new("Claude config file (~/.claude.json)", System.IO.Path.Combine(home, ".claude.json"), false),
            new("Claude Desktop (%LOCALAPPDATA%\\Claude)", System.IO.Path.Combine(localAppData, "Claude"), true),
            new("Claude Desktop (%APPDATA%\\Claude)", System.IO.Path.Combine(appData, "Claude"), true),
            new("Claude Desktop (%LOCALAPPDATA%\\AnthropicClaude)", System.IO.Path.Combine(localAppData, "AnthropicClaude"), true),
            new("Claude config (~/.config/claude)", System.IO.Path.Combine(home, ".config", "claude"), true),
        };
    }

    /// <summary>
    /// Pure existence filter over an arbitrary candidate list - this is the
    /// testable core of the scanner: given any list of (path, isDirectory)
    /// pairs (real known-candidate paths, or a test's own temp-directory
    /// stand-ins), return only the ones that exist. A candidate whose
    /// existence check itself throws (IOException / UnauthorizedAccessException
    /// - e.g. a denied parent directory) is treated as absent rather than
    /// letting one bad candidate take down the probe of every other one.
    /// </summary>
    public static IReadOnlyList<ClaudeLocation> ProbeCandidates(IEnumerable<ClaudeLocation> candidates)
    {
        var found = new List<ClaudeLocation>();
        foreach (var candidate in candidates)
        {
            try
            {
                var exists = candidate.IsDirectory
                    ? Directory.Exists(candidate.Path)
                    : File.Exists(candidate.Path);
                if (exists)
                    found.Add(candidate);
            }
            catch (Exception)
            {
                // Treat as absent - see doc comment above. Deliberately catches
                // everything (not just IOException/UnauthorizedAccessException):
                // File.Exists/Directory.Exists are documented to swallow most
                // access problems and return false already, but a malformed
                // candidate path (e.g. embedded invalid characters) can still
                // throw ArgumentException/PathTooLongException/NotSupportedException
                // depending on runtime and platform, and one bad candidate must
                // never take the whole probe down with it.
            }
        }
        return found;
    }

    /// <summary>
    /// Computes file count and total byte size for one location or tree node.
    /// Walks directories by hand (mirroring FileSelector's own approach)
    /// rather than Directory.EnumerateFiles(..., AllDirectories), for the
    /// same two reasons FileSelector does: one unreadable subdirectory must
    /// not abort the whole count (it is skipped, and enumeration continues),
    /// and a reparse point (symlink/junction) is never followed, so a link
    /// cycle cannot hang this or double-count a target reachable another way.
    ///
    /// Does no threading itself - this method is synchronous and can walk a
    /// large tree (skills/ was 4,941 files on the reference machine), so
    /// callers (the tree picker's UI adapter) are the ones responsible for
    /// running it off the UI thread. Never throws for an I/O reason (an
    /// unreadable path is simply skipped) - it DOES throw
    /// <see cref="OperationCanceledException"/> if <paramref
    /// name="cancellationToken"/> is cancelled mid-walk (fix round 1: the
    /// picker dialog cancels this on close, so a walk queued for a directory
    /// the user is no longer looking at - or the whole dialog having been
    /// dismissed - does not keep running to completion in the background).
    /// </summary>
    public static DirectoryStats ComputeStats(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        if (!isDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new DirectoryStats(1, new FileInfo(path).Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new DirectoryStats(0, 0);
            }
        }

        var count = 0;
        long bytes = 0;
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                if (!IsReparsePoint(sub))
                    stack.Push(sub);
            }

            foreach (var file in files)
            {
                if (IsReparsePoint(file))
                    continue;
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // One unreadable file must not drop the whole count.
                }
            }
        }

        return new DirectoryStats(count, bytes);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fail closed, same as FileSelector: if we cannot tell what it
            // is, do not walk into it and do not count it.
            return true;
        }
    }
}

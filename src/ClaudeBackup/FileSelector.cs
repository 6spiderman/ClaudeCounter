using System.Text.RegularExpressions;
using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Applies user include/exclude globs to files under a root directory, then
/// strips anything caught by the non-overridable <see cref="SecretDenylist"/>.
///
/// Implemented without Microsoft.Extensions.FileSystemGlobbing so this stays a
/// zero-new-dependency piece of code: adding a NuGet package here would mean
/// regenerating packages.lock.json (and passing --locked-mode restore plus the
/// vulnerable-package gate) for a single, small, easily hand-rolled matcher in
/// a security-critical path. See the task report for the full reasoning.
///
/// Walks the tree by hand (rather than Directory.EnumerateFiles(..., AllDirectories))
/// so it can refuse to follow reparse points (symlinks/junctions) - those can
/// point outside <c>root</c>, and a per-file FileInfo.ResolveLinkTarget check
/// is not enough because a file *inside* a linked directory is not itself a
/// link. A directory whose own path matches an exclude pattern is pruned
/// before its contents are enumerated, both for performance (a large excluded
/// subtree like "projects/**" is never walked) and so an unreadable directory
/// under it cannot abort the whole selection.
/// </summary>
public sealed class FileSelector
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Convenience overload for callers that do not care what the denylist withheld.</summary>
    public IReadOnlyList<string> Select(string root, IEnumerable<string> include, IEnumerable<string> exclude)
        => Select(root, include, exclude, out _);

    /// <summary>
    /// Returns root-relative, forward-slashed paths that match an include
    /// pattern, match no exclude pattern, are reachable without leaving
    /// <paramref name="root"/> through a symlink/junction, and are not
    /// caught by <see cref="SecretDenylist"/>. <paramref name="withheldBySecretDenylist"/>
    /// carries the paths that would otherwise have been selected but were
    /// dropped by the denylist, so a caller can log what was withheld instead
    /// of silently losing files a user's globs asked for.
    /// </summary>
    public IReadOnlyList<string> Select(
        string root,
        IEnumerable<string> include,
        IEnumerable<string> exclude,
        out List<string> withheldBySecretDenylist)
    {
        withheldBySecretDenylist = new List<string>();

        var includePatterns = include.Select(ToRegex).ToList();
        var excludePatterns = exclude.Select(ToRegex).ToList();

        if (includePatterns.Count == 0 || !Directory.Exists(root))
            return Array.Empty<string>();

        var realRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(realRoot);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // One unreadable directory (permissions, a device file, etc.)
                // must not abort the whole backup - skip it and keep going.
                Log.Warn($"FileSelector: skipping unreadable directory '{ToRelative(realRoot, dir)}'.");
                continue;
            }

            foreach (var sub in subDirs)
            {
                // Never follow a junction/symlink: it can point outside root,
                // and there is no cycle detection needed as long as we never
                // descend into one.
                if (IsReparsePoint(sub))
                    continue;

                var subRel = ToRelative(realRoot, sub);
                if (excludePatterns.Any(p => SafeIsMatch(p, subRel, timeoutMeansMatch: true)))
                    continue; // pruned: nothing under an excluded directory can be selected

                stack.Push(sub);
            }

            foreach (var file in files)
            {
                if (IsReparsePoint(file))
                    continue; // a symlinked file can equally point outside root

                var full = Path.GetFullPath(file);

                // Defense in depth: even though reparse points are already
                // skipped above, refuse anything that resolves outside root.
                if (!full.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rel = ToRelative(realRoot, full);

                if (!includePatterns.Any(p => SafeIsMatch(p, rel, timeoutMeansMatch: false)))
                    continue;
                if (excludePatterns.Any(p => SafeIsMatch(p, rel, timeoutMeansMatch: true)))
                    continue;

                if (SecretDenylist.IsSecret(rel))
                {
                    withheldBySecretDenylist.Add(rel);
                    continue;
                }

                results.Add(rel);
            }
        }

        results.Sort(StringComparer.Ordinal);
        withheldBySecretDenylist.Sort(StringComparer.Ordinal);
        return results;
    }

    private static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Fail closed: if we cannot tell what it is, do not follow or select it.
            return true;
        }
    }

    /// <summary>
    /// Pattern text comes from user-editable backup.json, so a pathological
    /// glob must not be able to hang the scheduled worker (ReDoS). Each Regex
    /// carries a match timeout; a timeout on an include pattern counts as "no
    /// match" (the file stays out), a timeout on an exclude pattern counts as
    /// "match" (the file stays out) - either way the failure mode favors
    /// leaving a file out over letting a runaway pattern hang the process.
    /// </summary>
    private static bool SafeIsMatch(Regex regex, string input, bool timeoutMeansMatch)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            Log.Warn($"FileSelector: pattern match timed out on '{input}'; " +
                     $"treating as {(timeoutMeansMatch ? "excluded" : "not included")}.");
            return timeoutMeansMatch;
        }
    }

    // Matches, in priority order at each position: a directory-spanning
    // globstar segment ("**/" or "/**"), a bare "**", a single-segment "*",
    // a single-character "?", or any other regex-meaningful character that
    // needs escaping. Ordering the alternation this way means the
    // multi-character globstar forms are consumed whole before the
    // single-"*"/"?" cases ever get a chance to split them apart.
    private static readonly Regex TokenRegex =
        new(@"\*\*/|/\*\*|\*\*|\*|\?|[.+^$(){}|\[\]]", RegexOptions.Compiled);

    /// <summary>
    /// Translates one glob pattern into an anchored, case-insensitive Regex.
    /// "**" spans directory separators, including matching zero directories
    /// at a "/**" or "**/" boundary (so "plugins/**/*.json" matches both
    /// "plugins/foo.json" and "plugins/sub/foo.json", and "projects/**"
    /// matches "projects" itself as well as everything under it). A single
    /// "*" matches within one path segment only; "?" matches exactly one
    /// non-"/" character. Everything else is literal. The pattern is
    /// pre-normalized to forward slashes, so a literal backslash never
    /// reaches the translator - there is nothing left needing a backslash
    /// escape rule.
    /// </summary>
    private static Regex ToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var pattern = TokenRegex.Replace(normalized, m => m.Value switch
        {
            "**/" => "(?:.*/)?",
            "/**" => "(?:/.*)?",
            "**" => ".*",
            "*" => "[^/]*",
            "?" => "[^/]",
            _ => "\\" + m.Value,
        });

        return new Regex(
            "^" + pattern + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            PatternTimeout);
    }
}

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
/// link. This is the actual containment mechanism: a directory that is a
/// reparse point is simply never entered, so nothing under it is ever
/// visited. (A same-volume NTFS *hard link* to a file outside root is not a
/// reparse point and is not caught by this - closing that would need a
/// file-ID/volume check, which is out of scope here; call this containment
/// against symlinks/junctions specifically, not an absolute guarantee.)
///
/// A directory is pruned (its contents never enumerated) only when an
/// exclude pattern that ends in literal "**" matches the directory's own
/// relative path - that suffix is what guarantees every possible descendant
/// also matches the same pattern, so pruning cannot silently drop a file the
/// pattern would not otherwise have excluded. A pattern that does not end in
/// "**" (e.g. "**/*cache*", which only constrains a file's own name) is left
/// to per-file matching instead. Every prune is logged so a future mistake
/// in this reasoning is observable rather than silent data loss.
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

        var includePatterns = include.Select(Compile).ToList();
        var excludePatterns = exclude.Select(Compile).ToList();

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
                if (TryFindPruningExclude(excludePatterns, subRel, out var matchedGlob))
                {
                    Log.Info($"FileSelector: pruning directory '{subRel}' (matched exclude pattern '{matchedGlob}').");
                    continue;
                }

                stack.Push(sub);
            }

            foreach (var file in files)
            {
                if (IsReparsePoint(file))
                    continue; // a symlinked file can equally point outside root

                var full = Path.GetFullPath(file);

                // Cheap backstop, not the primary defense: containment is
                // actually enforced by never entering a reparse-point
                // directory above. This just refuses anything that somehow
                // resolved outside root anyway.
                if (!full.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rel = ToRelative(realRoot, full);

                if (!includePatterns.Any(p => SafeIsMatch(p.Regex, rel, timeoutMeansMatch: false)))
                    continue;
                if (excludePatterns.Any(p => SafeIsMatch(p.Regex, rel, timeoutMeansMatch: true)))
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

    /// <summary>
    /// A directory can only be pruned wholesale by an exclude pattern whose
    /// text ends in literal "**" - that is exactly the shape that guarantees
    /// every descendant also matches the pattern. "**/*cache*" ends in
    /// "cache*", not "**": it constrains a file's own name, so a directory
    /// whose name merely contains "cache" (e.g. "cache-helpers") must NOT be
    /// pruned by it - files under it that do not themselves match get kept,
    /// exactly as round-0's flat enumeration did.
    /// </summary>
    private static bool TryFindPruningExclude(
        List<CompiledPattern> excludePatterns, string subRel, out string matchedGlob)
    {
        foreach (var p in excludePatterns)
        {
            if (!p.Glob.EndsWith("**", StringComparison.Ordinal))
                continue;
            if (SafeIsMatch(p.Regex, subRel, timeoutMeansMatch: true))
            {
                matchedGlob = p.Glob;
                return true;
            }
        }

        matchedGlob = "";
        return false;
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
    // single-"*"/"?" cases ever get a chance to split them apart. Pattern
    // text here is our own small, fixed, quantifier-free alternation of
    // ASCII literals - not user input - so it carries no match timeout.
    private static readonly Regex TokenRegex =
        new(@"\*\*/|/\*\*|\*\*|\*|\?|[.+^$(){}|\[\]]", RegexOptions.Compiled);

    private readonly record struct CompiledPattern(string Glob, Regex Regex);

    /// <summary>Normalizes and compiles one glob pattern, keeping the original text alongside the Regex.</summary>
    private static CompiledPattern Compile(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        return new CompiledPattern(normalized, ToRegex(normalized));
    }

    /// <summary>
    /// Translates one glob pattern into an anchored, case-insensitive Regex.
    /// "**" spans directory separators, including matching zero directories
    /// at a "/**" or "**/" boundary (so "plugins/**/*.json" matches both
    /// "plugins/foo.json" and "plugins/sub/foo.json", and "projects/**"
    /// matches "projects" itself as well as everything under it). A single
    /// "*" matches within one path segment only; "?" matches exactly one
    /// non-"/" character. Everything else is literal. The pattern is
    /// expected pre-normalized to forward slashes, so a literal backslash
    /// never reaches the translator - there is nothing left needing a
    /// backslash escape rule. User-supplied text from backup.json, so the
    /// resulting Regex carries a match timeout (see SafeIsMatch).
    /// </summary>
    private static Regex ToRegex(string normalizedGlob)
    {
        var pattern = TokenRegex.Replace(normalizedGlob, m => m.Value switch
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

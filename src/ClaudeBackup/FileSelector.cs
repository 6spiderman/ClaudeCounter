using System.Text.RegularExpressions;

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
/// </summary>
public sealed class FileSelector
{
    public IReadOnlyList<string> Select(string root,
        IEnumerable<string> include, IEnumerable<string> exclude)
    {
        var includePatterns = include.Select(ToRegex).ToList();
        var excludePatterns = exclude.Select(ToRegex).ToList();

        if (includePatterns.Count == 0 || !Directory.Exists(root))
            return Array.Empty<string>();

        var results = new List<string>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');

            if (!includePatterns.Any(p => p.IsMatch(rel)))
                continue;
            if (excludePatterns.Any(p => p.IsMatch(rel)))
                continue;

            // Non-overridable: applied last, after the user's include/exclude
            // globs, so no config can cause a secret to be selected for upload.
            if (SecretDenylist.IsSecret(rel))
                continue;

            results.Add(rel);
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    // Matches, in priority order at each position: a directory-spanning
    // globstar segment ("**/" or "/**"), a bare "**", a single-segment "*",
    // or any other regex-meaningful character that needs escaping. Ordering
    // the alternation this way means the multi-character globstar forms are
    // consumed whole before the single-"*" case ever gets a chance to split
    // them apart.
    private static readonly Regex TokenRegex =
        new(@"\*\*/|/\*\*|\*\*|\*|[.+^$(){}|\[\]\\]", RegexOptions.Compiled);

    /// <summary>
    /// Translates one glob pattern into an anchored, case-insensitive Regex.
    /// "**" spans directory separators, including matching zero directories
    /// at a "/**" or "**/" boundary (so "plugins/**/*.json" matches both
    /// "plugins/foo.json" and "plugins/sub/foo.json", and "projects/**"
    /// matches "projects" itself as well as everything under it). A single
    /// "*" matches within one path segment only. Everything else is literal.
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
            _ => "\\" + m.Value,
        });

        return new Regex("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ClaudeCounter.Core;

[assembly: InternalsVisibleTo("ClaudeCounter.Tests")]

namespace ClaudeBackup;

public sealed record BackendResult(bool Ok, string Message);

/// <summary>
/// Mirrors a selection of files into a local staging git repository and
/// pushes it to <see cref="GitTarget.RemoteUrl"/>. All git invocations go
/// through <see cref="IProcessRunner"/> so this is fully unit-testable
/// without a real git binary or a real remote.
///
/// Authentication relies entirely on the user's existing Git Credential
/// Manager (or an SSH agent) - this class never reads, stores, or logs a
/// credential, and never puts one on a command line. Every string that can
/// reach a log line, an exception message, or <see cref="BackendResult.Message"/>
/// is funneled through <see cref="Fail"/> or <see cref="Check"/>, both of
/// which scrub credential-bearing URL fragments via <see cref="ScrubCredentials"/>
/// before the text goes anywhere - this matters because a real `git push`
/// failure against an HTTPS remote with an embedded token commonly echoes
/// the full URL, credential included, back on stderr.
/// </summary>
public sealed class GitBackend
{
    private readonly IProcessRunner _runner;
    private readonly string _stagingDir;

    public GitBackend(IProcessRunner runner, string stagingDir)
    {
        _runner = runner;
        _stagingDir = stagingDir;
    }

    public BackendResult Run(string sourceRoot, IReadOnlyList<string> files, GitTarget target)
    {
        if (!_runner.Exists("git"))
            return Fail("git not found on PATH - install Git for Windows and ensure 'git' is available.");

        try
        {
            var stagingPreexisted = Directory.Exists(_stagingDir);
            Directory.CreateDirectory(_stagingDir);
            var hasGitDir = Directory.Exists(Path.Combine(_stagingDir, ".git"));

            // MirrorFiles below clears every non-.git entry in the staging
            // directory on every run so deletions propagate. If stagingDir
            // was ever pointed at a directory that already had unrelated
            // content and no .git folder (a misconfiguration - typo, or a
            // caller bug), that content would be silently destroyed. Refuse
            // instead of guessing.
            if (stagingPreexisted && !hasGitDir && Directory.EnumerateFileSystemEntries(_stagingDir).Any())
            {
                return Fail(
                    $"Staging directory '{_stagingDir}' already contains files but is not a git checkout - " +
                    "refusing to clear it. Point stagingDir at an empty directory or an existing git checkout.");
            }

            if (!hasGitDir)
            {
                Check(_runner.Run("git", new[] { "init", "-b", target.Branch }, _stagingDir), "git init");
                Check(_runner.Run("git", new[] { "remote", "add", "origin", target.RemoteUrl }, _stagingDir), "git remote add");
            }

            MirrorFiles(sourceRoot, files);

            Check(_runner.Run("git", new[] { "add", "-A" }, _stagingDir), "git add");

            // 'git diff --cached --quiet' exit code tells us whether there is
            // anything staged without parsing any locale-dependent
            // human-readable text: 0 = no staged changes (a legitimate no-op
            // backup run, not a failure), 1 = staged changes exist (proceed
            // to commit), anything else = the diff itself failed
            // unexpectedly. The earlier approach of matching the English
            // phrase "nothing to commit" in commit output would misreport a
            // no-op run as a failure under a non-English git locale, and
            // could equally misread a genuine commit failure as success if
            // its text happened to contain that phrase - this is exit-code
            // based instead, so it is immune to both.
            var diff = _runner.Run("git", new[] { "diff", "--cached", "--quiet" }, _stagingDir);
            if (diff.ExitCode == 0)
            {
                Log.Info("GitBackend: no changes to back up.");
            }
            else if (diff.ExitCode == 1)
            {
                var commit = _runner.Run("git",
                    new[] { "commit", "-m", $"Backup {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}" }, _stagingDir);
                if (!commit.Ok)
                    return Fail($"git commit failed: {commit.StdErr}".Trim());
            }
            else
            {
                return Fail($"git diff --cached failed: {diff.StdErr}".Trim());
            }

            Check(_runner.Run("git", new[] { "push", "origin", target.Branch }, _stagingDir), "git push");
            Log.Info($"GitBackend: push OK to '{RedactRemote(target.RemoteUrl)}' branch '{target.Branch}'.");
            return new BackendResult(true, "GitHub backup complete.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Replaces the staged working tree with exactly the current selection.
    /// Clearing everything but .git first (rather than only overwriting) is
    /// what makes deletions in the source propagate to the repo - otherwise
    /// a file removed from the selection would linger in the backup forever.
    /// </summary>
    private void MirrorFiles(string sourceRoot, IReadOnlyList<string> files)
    {
        var stagingFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_stagingDir));

        foreach (var entry in Directory.EnumerateFileSystemEntries(_stagingDir))
        {
            if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }

        foreach (var rel in files)
        {
            // Defense in depth: FileSelector is expected to hand back
            // root-relative, forward-slashed paths with no ".." segment, but
            // this backend must not assume that - a path that could escape
            // the staging directory must never be written anywhere.
            if (!IsSafeRelativePath(rel))
            {
                Log.Warn($"GitBackend: refusing unsafe relative path '{rel}'.");
                continue;
            }

            var relForFs = rel.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(sourceRoot, relForFs);
            var dst = Path.Combine(_stagingDir, relForFs);

            // Belt-and-braces alongside IsSafeRelativePath: resolve the
            // actual destination and verify it is still under the staging
            // directory before writing anything. Given IsSafeRelativePath's
            // segment-based checks, nothing it accepts can currently make it
            // here and still resolve outside stagingFull - see
            // GitBackendTests.IsWithinDirectoryDetectsEscapes, which tests
            // this containment check directly (including the classic
            // "C:\staging" vs "C:\staging-evil" prefix-collision case) rather
            // than via an end-to-end escape through Run, precisely because no
            // such escape exists today. That is what makes this a real
            // second layer instead of untested dead code.
            var dstFull = Path.GetFullPath(dst);
            if (!IsWithinDirectory(stagingFull, dstFull))
            {
                Log.Warn($"GitBackend: refusing path that resolves outside staging directory: '{rel}'.");
                continue;
            }

            if (!File.Exists(src))
            {
                Log.Warn($"GitBackend: source file missing, skipping: '{rel}'.");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }

    /// <summary>
    /// Rejects absolute paths, empty segments, and ".." segments so a
    /// malformed or malicious relative path can never write outside the
    /// staging directory (belt-and-braces alongside <see cref="IsWithinDirectory"/>
    /// in <see cref="MirrorFiles"/>).
    /// </summary>
    private static bool IsSafeRelativePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (Path.IsPathRooted(rel)) return false;
        if (rel.Contains('\\')) return false;

        foreach (var segment in rel.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="candidateFullPath"/> is <paramref name="baseDirFull"/>
    /// itself or somewhere underneath it. Separator-aware on purpose: a naive
    /// <c>StartsWith(baseDirFull)</c> would wrongly accept a sibling directory
    /// whose name happens to share the same prefix (e.g. "C:\staging-evil"
    /// against a base of "C:\staging") - see
    /// GitBackendTests.IsWithinDirectoryDetectsEscapes for that exact case.
    /// Internal (not private) so it can be tested directly.
    /// </summary>
    internal static bool IsWithinDirectory(string baseDirFull, string candidateFullPath)
    {
        var normalizedBase = Path.TrimEndingDirectorySeparator(baseDirFull);
        return string.Equals(candidateFullPath, normalizedBase, StringComparison.OrdinalIgnoreCase)
            || candidateFullPath.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void Check(ProcessResult r, string what)
    {
        if (!r.Ok)
            throw new InvalidOperationException($"{what} failed: {ScrubCredentials(r.StdErr)}".Trim());
    }

    /// <summary>
    /// Single choke point for anything that becomes a <see cref="BackendResult.Message"/>
    /// or a log line: scrubs credentials before logging and before returning,
    /// so no caller of Fail can forget to redact.
    /// </summary>
    private static BackendResult Fail(string message)
    {
        var scrubbed = ScrubCredentials(message);
        Log.Warn($"GitBackend: {scrubbed}");
        return new BackendResult(false, scrubbed);
    }

    // Matches "<scheme>://<userinfo>@" so a user:token (or bare token)
    // embedded in a URL can be stripped wherever it appears in free text -
    // not just a clean remote URL, but also a full sentence of git stderr
    // like "fatal: unable to access 'https://<token>@github.com/...': ...".
    // Requiring an explicit "scheme://" prefix is what keeps this from
    // mangling the SSH shorthand form ("git@github.com:user/repo.git"),
    // where "git@" is a username, not a credential, and there is no "://" to
    // match against. The userinfo group deliberately swallows an optional
    // ":password" segment too (":" is not excluded from the character
    // class) rather than capturing user and password separately - both are
    // credential-shaped and neither should ever survive into a log.
    private static readonly Regex CredentialUrlRegex = new(
        @"\b(?<scheme>https?|ssh)://[^\s/@]+@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strips credential-bearing "user:pass@" / "token@" prefixes out of any
    /// URL found in <paramref name="text"/>. Safe to call on arbitrary free
    /// text (git stderr, exception messages) as well as a bare URL; text
    /// with no matching pattern - including the SSH shorthand form - passes
    /// through unchanged. Null/empty input returns "" rather than throwing.
    /// Internal (not private) so it can be tested directly.
    /// </summary>
    internal static string ScrubCredentials(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return CredentialUrlRegex.Replace(text, m => $"{m.Groups["scheme"].Value}://");
    }

    /// <summary>
    /// Redacts a configured remote URL before it is ever written to the log
    /// (used on the one line that names <see cref="GitTarget.RemoteUrl"/>
    /// directly). Delegates to <see cref="ScrubCredentials"/> - kept as a
    /// separate name because callers reasoning about "redact the remote we
    /// are about to log" and "scrub whatever text git handed back" are
    /// different intents, even though the underlying mechanism is the same.
    /// Internal (not private) so it can be tested directly.
    /// </summary>
    internal static string RedactRemote(string? remoteUrl) => ScrubCredentials(remoteUrl);
}

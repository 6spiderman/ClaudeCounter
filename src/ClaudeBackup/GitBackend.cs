using System.Runtime.CompilerServices;
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
            else
            {
                // Reconcile the remote on every later run, not just at init.
                // 'git remote add' above only ever runs the first time this
                // staging dir is used - on every subsequent run hasGitDir is
                // true and that line never executes again. Without this,
                // repointing target.RemoteUrl in Settings would silently keep
                // pushing to the FIRST url ever configured, because 'origin'
                // resolves from backup-repo\.git\config on disk, not from the
                // live config passed in here. 'remote set-url' fails if no
                // 'origin' remote exists yet (e.g. the staging dir's .git was
                // created by something other than this class) - fall back to
                // 'remote add' in that case rather than treating it as fatal.
                var setUrl = _runner.Run("git", new[] { "remote", "set-url", "origin", target.RemoteUrl }, _stagingDir);
                if (!setUrl.Ok)
                    Check(_runner.Run("git", new[] { "remote", "add", "origin", target.RemoteUrl }, _stagingDir), "git remote add");
            }

            // Make a branch change take effect too: without this, changing
            // target.Branch on an existing staging dir would leave the local
            // checkout on whatever branch 'init -b' created the very first
            // time, and 'git push origin <newBranch>' would fail with
            // "src refspec <newBranch> does not match any" because no local
            // branch by that name exists. '-B' creates the branch if it does
            // not exist yet or resets it to HEAD if it does, so this is a
            // no-op in effect when already on target.Branch and otherwise
            // makes the switch happen before anything is staged.
            Check(_runner.Run("git", new[] { "checkout", "-B", target.Branch }, _stagingDir), "git checkout");

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
            // I2: this class has no way to call the GitHub API and confirm
            // the repo is actually private - the security model's guardrail
            // is a non-negotiable, and the one thing achievable without that
            // API call is making sure a successful push never implies privacy
            // was checked.
            Log.Warn("GitBackend: repo privacy cannot be verified automatically - confirm the remote repo is private.");
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
            if (!RelativePathGuard.IsSafe(rel))
            {
                Log.Warn($"GitBackend: refusing unsafe relative path '{rel}'.");
                continue;
            }

            // Fail-closed backstop at the actual point of write: FileSelector
            // already strips anything SecretDenylist flags before Run is ever
            // called, and BackupRunner re-checks the whole selection before
            // invoking any backend - but Run takes an arbitrary file list as
            // public API, so a secret-named entry reaching this method
            // directly (a future caller, a test, a bug upstream) must still
            // never be staged.
            if (SecretDenylist.IsSecret(rel))
            {
                Log.Warn($"GitBackend: refusing to stage secret-named file (denylist backstop): '{rel}'.");
                continue;
            }

            var relForFs = rel.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(sourceRoot, relForFs);
            var dst = Path.Combine(_stagingDir, relForFs);

            // Belt-and-braces alongside RelativePathGuard.IsSafe: resolve the
            // actual destination and verify it is still under the staging
            // directory before writing anything. Given IsSafe's
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

    /// <summary>
    /// Delegates to the shared <see cref="CredentialScrubber"/> so
    /// <see cref="RcloneBackend"/> uses the exact same regex instead of a
    /// second copy that could drift out of sync. Internal (not private) so
    /// it can be tested directly.
    /// </summary>
    internal static string ScrubCredentials(string? text) => CredentialScrubber.Scrub(text);

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

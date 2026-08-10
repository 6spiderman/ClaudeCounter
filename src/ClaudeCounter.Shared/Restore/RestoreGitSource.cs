using System.Globalization;
using System.Text.RegularExpressions;
using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Restore source for GitHub (design spec step 2/3, GitHub half): lists
/// backup-run commits via `git log` - each backup run is one commit (see
/// GitBackend), so a snapshot is a commit timestamp plus SHA - and
/// materialises a chosen commit by checking it out into a staging repo, then
/// copying its tree into a separate destination folder with the same
/// containment (rule 5) and denylist (rule 4) checks <see cref="RestoreZipSource"/>
/// applies to a Drive zip, so <see cref="RestoreClassifier"/> never has to
/// know or care which source a staged snapshot came from.
///
/// All git invocations go through <see cref="IProcessRunner"/>, exactly like
/// <see cref="GitBackend"/>, so this is fully unit-testable without a real
/// git binary or network access to a real remote. Every string that can
/// reach a result message or a log line is scrubbed via <see
/// cref="CredentialScrubber"/> first, for the same reason GitBackend does
/// this: a failed `git clone`/`git fetch` against an HTTPS remote with an
/// embedded token commonly echoes the full URL, credential included, back on
/// stderr.
/// </summary>
public sealed class RestoreGitSource
{
    private readonly IProcessRunner _runner;
    private readonly string _stagingDir;

    public RestoreGitSource(IProcessRunner runner, string stagingDir)
    {
        _runner = runner;
        _stagingDir = stagingDir;
    }

    // %x1f is the ASCII unit separator - it cannot appear in ordinary commit
    // subject text, so splitting on it (rather than a comma, pipe, or tab)
    // cannot be confused by a commit message that happens to contain the
    // delimiter character itself. %cI is the committer date in strict ISO
    // 8601 (unambiguous, and what GitBackend's own commit already produces
    // wall-clock content for - see GitBackend.Run's commit message).
    private const string LogFormat = "%H%x1f%cI%x1f%s";

    private static readonly char[] FieldSeparator = { '\u001f' };

    /// <summary>Lists backup-run commits on <paramref name="target"/>'s branch, newest first.</summary>
    public RestoreListResult ListSnapshots(GitTarget target)
    {
        if (!_runner.Exists("git"))
            return RestoreListResult.Failure(ScrubAndLog("git not found on PATH - install Git for Windows and ensure 'git' is available."));

        var (ok, message) = EnsureRepo(target);
        if (!ok)
            return RestoreListResult.Failure(message);

        var log = _runner.Run("git", new[] { "log", $"origin/{target.Branch}", $"--format={LogFormat}" }, _stagingDir);
        if (!log.Ok)
            return RestoreListResult.Failure(ScrubAndLog($"git log failed: {log.StdErr.Trim()}"));

        return RestoreListResult.Success(ParseGitLog(log.StdOut));
    }

    /// <summary>
    /// Checks out <paramref name="snapshotId"/> (a full commit SHA, as
    /// returned by <see cref="ListSnapshots"/>) into the staging repo, then
    /// copies its tree - excluding .git - into <paramref name="destinationDir"/>
    /// with the containment and denylist checks described on this class.
    /// Uses a detached checkout so materialising a snapshot never moves or
    /// creates any local branch.
    /// </summary>
    public RestoreMaterializeResult Materialize(GitTarget target, string snapshotId, string destinationDir)
    {
        if (!_runner.Exists("git"))
            return RestoreMaterializeResult.Failure(ScrubAndLog("git not found on PATH - install Git for Windows and ensure 'git' is available."));

        if (!IsPlausibleSha(snapshotId))
            return RestoreMaterializeResult.Failure("Invalid snapshot id.");

        var (ok, message) = EnsureRepo(target);
        if (!ok)
            return RestoreMaterializeResult.Failure(message);

        try
        {
            // --detach: never touches any local branch. --force: the staging
            // repo's worktree may still hold a previous Materialize call's
            // checked-out state; discard it rather than fail on local
            // changes that are, by construction, never meant to be kept.
            var checkout = _runner.Run("git", new[] { "checkout", "--force", "--detach", snapshotId }, _stagingDir);
            if (!checkout.Ok)
                return RestoreMaterializeResult.Failure(ScrubAndLog($"git checkout failed: {checkout.StdErr.Trim()}"));

            Directory.CreateDirectory(destinationDir);
            CopySafely(_stagingDir, destinationDir);

            return RestoreMaterializeResult.Success(destinationDir);
        }
        catch (Exception ex)
        {
            return RestoreMaterializeResult.Failure(ScrubAndLog(ex.Message));
        }
    }

    /// <summary>
    /// Clones into <see cref="_stagingDir"/> if it is not already a git
    /// checkout, or reconciles the remote and fetches the branch if it is -
    /// mirrors GitBackend.Run's own init-or-reconcile shape, including its
    /// guard against clearing a pre-existing, populated, non-git directory
    /// (a typo'd or reused staging path must not have its contents silently
    /// clobbered by a clone).
    /// </summary>
    private (bool Ok, string Message) EnsureRepo(GitTarget target)
    {
        try
        {
            var stagingPreexisted = Directory.Exists(_stagingDir);
            Directory.CreateDirectory(_stagingDir);
            var hasGitDir = Directory.Exists(Path.Combine(_stagingDir, ".git"));

            if (stagingPreexisted && !hasGitDir && Directory.EnumerateFileSystemEntries(_stagingDir).Any())
            {
                return (false, ScrubAndLog(
                    $"Restore staging directory '{_stagingDir}' already contains files but is not a git checkout - " +
                    "refusing to use it. Point it at an empty directory or an existing git checkout."));
            }

            if (!hasGitDir)
            {
                var clone = _runner.Run("git", new[] { "clone", target.RemoteUrl, "." }, _stagingDir);
                if (!clone.Ok)
                    return (false, ScrubAndLog($"git clone failed: {clone.StdErr.Trim()}"));
            }
            else
            {
                var setUrl = _runner.Run("git", new[] { "remote", "set-url", "origin", target.RemoteUrl }, _stagingDir);
                if (!setUrl.Ok)
                    _runner.Run("git", new[] { "remote", "add", "origin", target.RemoteUrl }, _stagingDir);

                var fetch = _runner.Run("git", new[] { "fetch", "origin", target.Branch }, _stagingDir);
                if (!fetch.Ok)
                    return (false, ScrubAndLog($"git fetch failed: {fetch.StdErr.Trim()}"));
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ScrubAndLog(ex.Message));
        }
    }

    /// <summary>
    /// Copies every file under the checked-out worktree at <paramref
    /// name="sourceRoot"/> (excluding .git) into <paramref
    /// name="destinationDir"/>, refusing (and logging) anything that fails
    /// restore rule 5 (containment) or restore rule 4 (the secret denylist)
    /// instead of copying it - the same discipline RestoreZipSource.ExtractSafely
    /// applies to a zip entry, applied here to a git-checked-out file. A
    /// well-formed git tree cannot itself contain a ".." path segment, but
    /// this does not trust that invariant - the check runs unconditionally.
    /// </summary>
    private static void CopySafely(string sourceRoot, string destinationDir)
    {
        var sourceRootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var destRootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDir));

        foreach (var file in EnumerateWorktreeFiles(sourceRootFull))
        {
            var rel = Path.GetRelativePath(sourceRootFull, file).Replace('\\', '/');

            if (!RelativePathGuard.IsSafe(rel))
            {
                Log.Warn($"RestoreGitSource: refusing unsafe checked-out path: '{rel}'.");
                continue;
            }

            if (SecretDenylist.IsSecret(rel))
            {
                Log.Warn($"RestoreGitSource: refusing to materialise denylisted file: '{rel}'.");
                continue;
            }

            var destPath = Path.Combine(destinationDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var destFull = Path.GetFullPath(destPath);

            if (!RelativePathGuard.IsWithinDirectory(destRootFull, destFull))
            {
                Log.Warn($"RestoreGitSource: refusing checked-out path that resolves outside the destination: '{rel}'.");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
            File.Copy(file, destFull, overwrite: true);
        }
    }

    private static IEnumerable<string> EnumerateWorktreeFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"RestoreGitSource: skipping unreadable directory '{dir}'.");
                continue;
            }

            foreach (var sub in subDirs)
            {
                if (string.Equals(Path.GetFileName(sub), ".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push(sub);
            }

            foreach (var file in files)
                yield return file;
        }
    }

    /// <summary>
    /// snapshotId is public API input, not trusted to already be a real SHA
    /// this class produced - rejecting anything that is not plain hex before
    /// it is ever passed as a `git checkout` argument closes off an argument-
    /// injection vector (a value like "--upload-pack=..." or one starting
    /// with "-") in addition to being a basic sanity check.
    /// </summary>
    private static bool IsPlausibleSha(string snapshotId) =>
        !string.IsNullOrWhiteSpace(snapshotId) && ShaPattern.IsMatch(snapshotId);

    private static readonly Regex ShaPattern = new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

    /// <summary>
    /// Parses `git log --format=%H%x1f%cI%x1f%s` output into <see
    /// cref="RestoreSnapshot"/>. Tolerant of a malformed line (skips just
    /// that line rather than throwing) since this is parsing another
    /// process's output, not a format this class fully controls end to end.
    /// </summary>
    private static List<RestoreSnapshot> ParseGitLog(string stdout)
    {
        var result = new List<RestoreSnapshot>();
        if (string.IsNullOrWhiteSpace(stdout))
            return result;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            var parts = line.Split(FieldSeparator, StringSplitOptions.None);
            if (parts.Length < 2)
                continue;

            var sha = parts[0];
            if (!IsPlausibleSha(sha))
                continue;

            if (!DateTimeOffset.TryParse(
                    parts[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                continue;

            var subject = parts.Length > 2 ? parts[2] : "";
            var shortSha = sha.Length > 7 ? sha[..7] : sha;
            var display = string.IsNullOrEmpty(subject) ? shortSha : $"{shortSha} - {subject}";

            result.Add(new RestoreSnapshot(sha, timestamp, display, null));
        }

        // `git log` already emits newest-first by default - re-sorting is
        // defense against relying on that ordering implicitly rather than
        // something expected to ever actually reorder real output.
        return result.OrderByDescending(s => s.Timestamp).ToList();
    }

    private static string ScrubAndLog(string message)
    {
        var scrubbed = CredentialScrubber.Scrub(message);
        Log.Warn($"RestoreGitSource: {scrubbed}");
        return scrubbed;
    }
}

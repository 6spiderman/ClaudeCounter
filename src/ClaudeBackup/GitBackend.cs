using ClaudeCounter.Core;

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
/// credential, and never puts one on a command line.
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
            Directory.CreateDirectory(_stagingDir);
            if (!Directory.Exists(Path.Combine(_stagingDir, ".git")))
            {
                Check(_runner.Run("git", new[] { "init", "-b", target.Branch }, _stagingDir), "git init");
                Check(_runner.Run("git", new[] { "remote", "add", "origin", target.RemoteUrl }, _stagingDir), "git remote add");
            }

            MirrorFiles(sourceRoot, files);

            Check(_runner.Run("git", new[] { "add", "-A" }, _stagingDir), "git add");

            // git commit exits non-zero when there is nothing staged to
            // commit - that is success (no changes since last backup), not a
            // failure. Detected via stdout text rather than exit code alone
            // because the fake runner in tests always returns exit code 0,
            // and a real "nothing to commit" run must still be told apart
            // from a genuine commit failure (bad author config, hook
            // rejection, etc.) which also returns non-zero with different
            // stdout/stderr.
            var commit = _runner.Run("git",
                new[] { "commit", "-m", $"Backup {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}" }, _stagingDir);
            var nothingToCommit =
                commit.StdOut.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) ||
                commit.StdErr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase);
            if (!commit.Ok && !nothingToCommit)
                return Fail($"git commit failed: {commit.StdErr}".Trim());

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

            var dstFull = Path.GetFullPath(dst);
            if (!dstFull.StartsWith(stagingFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
    /// staging directory (belt-and-braces alongside the full-path check in
    /// <see cref="MirrorFiles"/>).
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

    private static void Check(ProcessResult r, string what)
    {
        if (!r.Ok)
            throw new InvalidOperationException($"{what} failed: {r.StdErr}".Trim());
    }

    private static BackendResult Fail(string message)
    {
        Log.Warn($"GitBackend: {message}");
        return new BackendResult(false, message);
    }

    /// <summary>
    /// Strips userinfo (e.g. the token in "https://user:token@github.com/...")
    /// before a remote URL is ever written to the log, so a credential
    /// embedded in the configured remote can never end up in a log file.
    /// </summary>
    private static string RedactRemote(string remoteUrl)
    {
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            return builder.Uri.ToString();
        }
        return remoteUrl;
    }
}

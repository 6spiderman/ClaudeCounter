// tests/ClaudeCounter.Tests/ClaudeBackupTests/GitBackendTests.cs
using ClaudeBackup;
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class GitBackendTests : IDisposable
{
    // Default DiffExitCode = 1 (staged changes present) matches the common
    // "there is something to back up" case, so tests that do not care about
    // the nothing-to-commit path (most of them) still see a normal
    // add -> diff -> commit -> push flow without having to configure it.
    // "git init" creates a real .git directory under the working dir, same
    // as real git would - GitBackend uses physical .git presence to decide
    // both whether to re-init and (as of the staging-dir guard) whether a
    // pre-existing populated directory is safe to clear, so a fake that
    // left no trace on disk would make a second Run() against the same
    // staging dir behave unlike a real repeated run.
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool GitPresent { get; set; } = true;
        public int DiffExitCode { get; set; } = 1;
        public bool Exists(string file) => file == "git" ? GitPresent : true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");
            if (args.Count > 0 && args[0] == "init" && wd is not null)
                Directory.CreateDirectory(Path.Combine(wd, ".git"));
            if (args.Count > 0 && args[0] == "diff")
                return new ProcessResult(DiffExitCode, "", "");
            return new ProcessResult(0, "", "");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gbsrc-{Guid.NewGuid():N}");
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"gbstg-{Guid.NewGuid():N}");

    public GitBackendTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_staging)) Directory.Delete(_staging, true);
    }

    [Fact]
    public void MissingGitFailsCleanly()
    {
        var runner = new FakeRunner { GitPresent = false };
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.False(result.Ok);
        Assert.Contains("git", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopiesFilesAndPushes()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_staging, "settings.json")));
        Assert.Contains(runner.Calls, c => c.StartsWith("git add"));
        Assert.Contains(runner.Calls, c => c.Contains("commit"));
        Assert.Contains(runner.Calls, c => c.Contains("push"));
    }

    // 'git diff --cached --quiet' exiting 0 (no staged changes) is the
    // signal for "nothing to back up this run" - a legitimate no-op, not a
    // failure. Note stdout/stderr are both empty here: unlike the old
    // text-matching approach, detection needs no human-readable phrase at
    // all, which is what makes it locale-proof.
    private sealed class NothingToCommitRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");
            if (args.Count > 0 && args[0] == "diff")
                return new ProcessResult(0, "", ""); // no staged changes
            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public void NothingToCommitIsSuccessNotFailure()
    {
        var runner = new NothingToCommitRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.True(result.Ok);
        Assert.Contains(runner.Calls, c => c.Contains("push"));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git commit"));
    }

    // A genuine commit failure (e.g. a rejected hook) with staged changes
    // present must still be reported as a failure.
    private sealed class CommitFailsRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            if (args.Count > 0 && args[0] == "diff")
                return new ProcessResult(1, "", ""); // staged changes present
            if (args.Count > 0 && args[0] == "commit")
                return new ProcessResult(1, "", "hook declined");
            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public void RealCommitFailureIsReported()
    {
        var runner = new CommitFailsRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.False(result.Ok);
        Assert.Contains("commit", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Regression guard for the old text-matching approach: a commit failure
    // whose text is entirely non-English (no occurrence of "nothing to
    // commit" or any other English phrase) must still be recognized as a
    // failure, because detection is exit-code based, never text based.
    private sealed class NonEnglishCommitFailureRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            if (args.Count > 0 && args[0] == "diff")
                return new ProcessResult(1, "", "");
            if (args.Count > 0 && args[0] == "commit")
                return new ProcessResult(1, "", "erreur: le hook pre-commit a echoue");
            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public void NonEnglishCommitFailureIsStillReportedAsFailure()
    {
        var runner = new NonEnglishCommitFailureRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.False(result.Ok);
    }

    // 'git diff --cached --quiet' itself can fail for reasons unrelated to
    // "nothing staged" (corrupt repo, not actually a git directory, etc.) -
    // any exit code other than 0 or 1 must be reported as a failure, not
    // silently treated as either success or "proceed to commit".
    private sealed class DiffErrorsRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            if (args.Count > 0 && args[0] == "diff")
                return new ProcessResult(128, "", "fatal: not a git repository");
            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public void DiffFailureIsReportedNotSilentlyTreatedAsNoChanges()
    {
        var runner = new DiffErrorsRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.False(result.Ok);
    }

    // Fail-closed backstop at the point of write: FileSelector and
    // BackupRunner both already filter secrets out upstream, but Run is
    // public and takes an arbitrary file list, so a secret-named entry
    // reaching MirrorFiles directly must never be staged.
    [Fact]
    public void RefusesToStageSecretNamedFileEvenIfPassedDirectly()
    {
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };

        var result = backend.Run(_root, new[] { "settings.json", ".credentials.json" }, target);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_staging, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_staging, ".credentials.json")));
    }

    // Security: a path escaping the staging directory (e.g. via ".." even
    // though FileSelector should never emit one) must never be written
    // outside the staging directory, and must not throw - it should be
    // skipped so the rest of the backup can still proceed.
    [Fact]
    public void RefusesToStageFileOutsideStagingDirectory()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };

        var result = backend.Run(_root, new[] { "settings.json", "../escape.json" }, target);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_staging, "settings.json")));
        var escapePath = Path.Combine(Path.GetDirectoryName(_staging)!, "escape.json");
        Assert.False(File.Exists(escapePath));
    }

    // The full-path containment check (IsWithinDirectory) is a second,
    // independent layer behind IsSafeRelativePath's segment-based rejection.
    // Given IsSafeRelativePath's current rules, nothing it accepts can still
    // resolve outside the staging directory, so this layer is tested
    // directly rather than by hunting for an end-to-end bypass through Run
    // (which would mean the first layer has a hole - it does not). This also
    // covers the classic prefix-collision bug: a naive StartsWith check
    // would wrongly treat "C:\staging-evil" as being inside "C:\staging".
    [Theory]
    [InlineData(@"C:\staging", @"C:\staging\file.json", true)]
    [InlineData(@"C:\staging", @"C:\staging\sub\file.json", true)]
    [InlineData(@"C:\staging", @"C:\staging", true)]
    [InlineData(@"C:\staging", @"C:\other\file.json", false)]
    [InlineData(@"C:\staging", @"C:\staging-evil\file.json", false)]
    public void IsWithinDirectoryDetectsEscapes(string baseDir, string candidate, bool expected) =>
        Assert.Equal(expected, GitBackend.IsWithinDirectory(baseDir, candidate));

    // Deletions in the selection must propagate: a file present from a
    // previous run but no longer in the current selection must disappear
    // from the staging tree (and therefore the repo), not linger forever.
    [Fact]
    public void RemovesStaleFilesNoLongerInSelection()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };

        backend.Run(_root, new[] { "settings.json" }, target);
        Assert.True(File.Exists(Path.Combine(_staging, "settings.json")));

        // Second run selects nothing - the previously-staged file must go away.
        var result = backend.Run(_root, Array.Empty<string>(), target);

        Assert.True(result.Ok);
        Assert.False(File.Exists(Path.Combine(_staging, "settings.json")));
    }

    // Guard against clearing a pre-existing, populated, non-git directory:
    // if stagingDir is ever pointed at the wrong place (typo, caller bug),
    // MirrorFiles's "delete everything but .git" logic must not destroy
    // whatever was already there.
    [Fact]
    public void RefusesToClearPrePopulatedNonGitStagingDirectory()
    {
        Directory.CreateDirectory(_staging);
        File.WriteAllText(Path.Combine(_staging, "unexpected.txt"), "do not delete me");

        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };

        var result = backend.Run(_root, new[] { "settings.json" }, target);

        Assert.False(result.Ok);
        Assert.True(File.Exists(Path.Combine(_staging, "unexpected.txt")));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git init"));
    }

    // C1 regression: the remote must be reconciled on every run, not just at
    // init. Without 'remote set-url' on the second and later runs, a user
    // who repoints RemoteUrl in Settings would keep pushing to the FIRST url
    // ever configured for this staging dir, silently, forever.
    [Fact]
    public void SecondRunWithDifferentRemoteUrlReconciles()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var firstTarget = new GitTarget { Enabled = true, RemoteUrl = "https://github.com/org/first.git", Branch = "main" };
        backend.Run(_root, new[] { "settings.json" }, firstTarget);

        runner.Calls.Clear();
        var secondTarget = new GitTarget { Enabled = true, RemoteUrl = "https://github.com/org/second.git", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, secondTarget);

        Assert.True(result.Ok);
        Assert.Contains(runner.Calls,
            c => c.Contains("remote set-url origin https://github.com/org/second.git"));
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git init"));
    }

    // C1: changing Branch on an existing staging dir must actually switch
    // the local checkout, not just leave it on whatever 'init -b' created
    // the first time - otherwise 'git push origin <newBranch>' fails with
    // "src refspec ... does not match any".
    [Fact]
    public void SecondRunWithDifferentBranchChecksOutNewBranch()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var firstTarget = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        backend.Run(_root, new[] { "settings.json" }, firstTarget);

        runner.Calls.Clear();
        var secondTarget = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "backup-branch" };
        var result = backend.Run(_root, new[] { "settings.json" }, secondTarget);

        Assert.True(result.Ok);
        Assert.Contains(runner.Calls, c => c.Contains("checkout -B backup-branch"));
        Assert.Contains(runner.Calls, c => c.Contains("push origin backup-branch"));
    }

    // I2: GitBackend cannot call the GitHub API to confirm a repo is
    // actually private, so a successful push must still log that privacy was
    // never verified, rather than implying everything was checked.
    [Fact]
    public void SuccessfulPushLogsThatPrivacyIsUnverified()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };

        var before = ReadLog().Length;
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        var written = ReadLog()[before..];

        Assert.True(result.Ok);
        Assert.Contains("privacy cannot be verified", written);
    }

    private static string ReadLog()
    {
        if (Log.FilePath is not { } path || !File.Exists(path))
            return string.Empty;

        // Opened share-all: other tests in this assembly may write to the
        // same log concurrently - see BackupRunnerTests.ReadLog for the same
        // pattern.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Does not embed any Claude/Anthropic attribution in generated commit
    // message text - this project's global constraint applies to runtime
    // output, not just to this repo's own git history.
    [Fact]
    public void GeneratedCommitMessageHasNoAiAttribution()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        backend.Run(_root, new[] { "settings.json" }, target);

        var commitCall = Assert.Single(runner.Calls, c => c.Contains("commit -m"));
        Assert.DoesNotContain("Claude", commitCall, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Anthropic", commitCall, StringComparison.OrdinalIgnoreCase);
    }

    // Credential scrubbing: RedactRemote must strip "user:token@" / "token@"
    // out of an HTTPS remote, must NOT mangle the SSH shorthand form (that
    // "git@" is a username, not a secret), must pass through text with no
    // matching pattern unchanged, and must not throw on empty/null input.
    [Theory]
    [InlineData("https://user:token@github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("https://token@github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    [InlineData("not a url at all", "not a url at all")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void RedactRemoteStripsCredentialsButPreservesSshShorthand(string? input, string expected) =>
        Assert.Equal(expected, GitBackend.RedactRemote(input));

    // The critical end-to-end proof: a simulated 'git push' failure whose
    // stderr echoes an embedded-credential URL (exactly what a real HTTPS
    // 403/401 against a token remote looks like) must not put the token
    // into the BackendResult.Message that Run returns - that message is
    // what gets logged and is what Task 8 will likely surface again.
    private sealed class PushFailsWithCredentialRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            if (args.Count > 0 && args[0] == "diff")
                return new ProcessResult(1, "", ""); // staged changes present
            if (args.Count > 0 && args[0] == "push")
                return new ProcessResult(128, "",
                    "fatal: unable to access 'https://supersecrettoken123@github.com/org/repo.git/': " +
                    "The requested URL returned error: 403");
            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public void PushFailureNeverLeaksCredentialInResultMessage()
    {
        var runner = new PushFailsWithCredentialRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };

        var result = backend.Run(_root, new[] { "settings.json" }, target);

        Assert.False(result.Ok);
        Assert.DoesNotContain("supersecrettoken123", result.Message);
        Assert.Contains("github.com", result.Message);
    }

    // Same proof for the commit-failure path, which routes through Fail
    // directly rather than through Check - both choke points must scrub.
    private sealed class CommitFailsWithCredentialRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            if (args.Count > 0 && args[0] == "diff")
                return new ProcessResult(1, "", "");
            if (args.Count > 0 && args[0] == "commit")
                return new ProcessResult(1, "", "remote: https://usr:sekrit@github.com/org/repo.git rejected");
            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public void CommitFailureNeverLeaksCredentialInResultMessage()
    {
        var runner = new CommitFailsWithCredentialRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };

        var result = backend.Run(_root, new[] { "settings.json" }, target);

        Assert.False(result.Ok);
        Assert.DoesNotContain("sekrit", result.Message);
    }
}

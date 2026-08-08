// tests/ClaudeCounter.Tests/ClaudeBackupTests/GitBackendTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class GitBackendTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool GitPresent { get; set; } = true;
        public bool Exists(string file) => file == "git" ? GitPresent : true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");
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

    // Not in the original brief: a "nothing to commit" exit from git commit
    // (real git's actual behavior when the tree is unchanged) must be
    // reported as a successful backup run, not an error - otherwise every
    // no-op scheduled run would look like a failure.
    private sealed class NothingToCommitRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");
            if (args.Count > 0 && args[0] == "commit")
                return new ProcessResult(1, "nothing to commit, working tree clean", "");
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
    }

    // A genuine commit failure (e.g. a rejected hook) must still be reported
    // as a failure - only the specific "nothing to commit" case is success.
    private sealed class CommitFailsRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
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
}

// tests/ClaudeCounter.Tests/RestoreTests/RestoreGitSourceTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

public class RestoreGitSourceTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool GitPresent { get; set; } = true;
        public bool CloneShouldFail { get; set; }
        public string CloneStdErr { get; set; } = "";
        public bool FetchShouldFail { get; set; }
        public bool CheckoutShouldFail { get; set; }
        public string CheckoutStdErr { get; set; } = "";
        public string LogStdOut { get; set; } = "";

        /// <summary>Simulates the effect of a real checkout by populating the working dir on demand.</summary>
        public Action<string>? OnCheckout { get; set; }

        public bool Exists(string file) => file == "git" ? GitPresent : true;

        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");

            if (args.Count > 0 && args[0] == "clone")
            {
                if (CloneShouldFail) return new ProcessResult(128, "", CloneStdErr);
                if (wd is not null) Directory.CreateDirectory(Path.Combine(wd, ".git"));
                return new ProcessResult(0, "", "");
            }
            if (args.Count > 0 && args[0] == "fetch")
                return FetchShouldFail ? new ProcessResult(1, "", "fetch failed") : new ProcessResult(0, "", "");
            if (args.Count > 0 && args[0] == "log")
                return new ProcessResult(0, LogStdOut, "");
            if (args.Count > 0 && args[0] == "checkout")
            {
                if (CheckoutShouldFail) return new ProcessResult(1, "", CheckoutStdErr);
                if (wd is not null) OnCheckout?.Invoke(wd);
                return new ProcessResult(0, "", "");
            }
            return new ProcessResult(0, "", "");
        }
    }

    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"rgsstg-{Guid.NewGuid():N}");
    private readonly string _dest = Path.Combine(Path.GetTempPath(), $"rgsdst-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_staging)) Directory.Delete(_staging, true);
        if (Directory.Exists(_dest)) Directory.Delete(_dest, true);
    }

    private static readonly GitTarget Target = new() { Enabled = true, RemoteUrl = "url", Branch = "main" };

    // --- Listing ---------------------------------------------------------

    [Fact]
    public void ListSnapshotsFailsCleanlyWhenGitMissing()
    {
        var runner = new FakeRunner { GitPresent = false };
        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.False(result.Ok);
        Assert.Contains("git", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListSnapshotsFailsCleanlyWhenCloneFails()
    {
        var runner = new FakeRunner { CloneShouldFail = true, CloneStdErr = "fatal: repository not found" };
        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.False(result.Ok);
        Assert.Contains("repository not found", result.Message);
    }

    [Fact]
    public void ListSnapshotsFetchesInsteadOfCloningOnAnExistingCheckout()
    {
        Directory.CreateDirectory(Path.Combine(_staging, ".git"));
        var runner = new FakeRunner();

        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.True(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git clone"));
        Assert.Contains(runner.Calls, c => c.StartsWith("git fetch"));
    }

    [Fact]
    public void ListSnapshotsFailsCleanlyWhenFetchFails()
    {
        Directory.CreateDirectory(Path.Combine(_staging, ".git"));
        var runner = new FakeRunner { FetchShouldFail = true };

        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.False(result.Ok);
        Assert.Contains("fetch failed", result.Message);
    }

    // Parses `git log --format=%H%x1f%cI%x1f%s` output (fixture text, via
    // the fake runner) into snapshots, newest first.
    [Fact]
    public void ListSnapshotsParsesGitLogFixture()
    {
        const string sha1 = "1111111111111111111111111111111111aaaa";
        const string sha2 = "2222222222222222222222222222222222bbbb";
        var older = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

        // Deliberately out of chronological order in the fixture text, to
        // prove the parser sorts rather than trusting input order.
        var logOutput =
            $"{sha1}\u001f{older:o}\u001fBackup 2026-01-01 09:00:00\n" +
            $"{sha2}\u001f{newer:o}\u001fBackup 2026-02-01 09:00:00\n";

        var runner = new FakeRunner { LogStdOut = logOutput };
        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Snapshots.Count);
        Assert.Equal(sha2, result.Snapshots[0].Id);
        Assert.Equal(sha1, result.Snapshots[1].Id);
        Assert.Contains(sha2[..7], result.Snapshots[0].DisplayName);
        Assert.Contains("Backup 2026-02-01", result.Snapshots[0].DisplayName);
        Assert.Null(result.Snapshots[0].SizeBytes);
    }

    [Fact]
    public void ListSnapshotsSkipsMalformedLinesRatherThanThrowing()
    {
        const string sha = "3333333333333333333333333333333333cccc";
        var timestamp = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var logOutput = "not-a-well-formed-line\n" + $"{sha}\u001f{timestamp:o}\u001fReal backup\n";

        var runner = new FakeRunner { LogStdOut = logOutput };
        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.True(result.Ok);
        Assert.Single(result.Snapshots);
        Assert.Equal(sha, result.Snapshots[0].Id);
    }

    // rclone/git stderr can echo a remote spec verbatim on failure - the
    // same credential-scrubbing discipline GitBackend applies must hold here.
    [Fact]
    public void CloneFailureScrubsCredentialFromRemoteUrlInResultMessage()
    {
        var runner = new FakeRunner
        {
            CloneShouldFail = true,
            CloneStdErr = "fatal: unable to access 'https://supersecrettoken@github.com/org/repo.git/': 403",
        };

        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.False(result.Ok);
        Assert.DoesNotContain("supersecrettoken", result.Message);
        Assert.Contains("github.com", result.Message);
    }

    // --- Materialize -------------------------------------------------------

    [Fact]
    public void MaterializeFailsCleanlyWhenGitMissing()
    {
        var runner = new FakeRunner { GitPresent = false };
        var result = new RestoreGitSource(runner, _staging).Materialize(
            Target, "1111111111111111111111111111111111aaaa", _dest);

        Assert.False(result.Ok);
        Assert.Contains("git", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("not-hex-zzzz")]
    [InlineData("")]
    [InlineData("../escape")]
    public void MaterializeRejectsAnImplausibleSnapshotId(string snapshotId)
    {
        var runner = new FakeRunner();
        var result = new RestoreGitSource(runner, _staging).Materialize(Target, snapshotId, _dest);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git checkout"));
    }

    [Fact]
    public void MaterializeFailsCleanlyWhenCheckoutFails()
    {
        var runner = new FakeRunner { CheckoutShouldFail = true, CheckoutStdErr = "fatal: reference is not a tree" };
        var result = new RestoreGitSource(runner, _staging).Materialize(
            Target, "1111111111111111111111111111111111aaaa", _dest);

        Assert.False(result.Ok);
        Assert.Contains("reference is not a tree", result.Message);
    }

    [Fact]
    public void MaterializeCopiesCheckedOutFilesExcludingDotGit()
    {
        var runner = new FakeRunner
        {
            OnCheckout = wd =>
            {
                File.WriteAllText(Path.Combine(wd, "settings.json"), "{}");
                Directory.CreateDirectory(Path.Combine(wd, "commands"));
                File.WriteAllText(Path.Combine(wd, "commands", "a.md"), "hello");
                // Simulate git's own internal state alongside the checked-out
                // tree - it must never be copied into the materialised folder.
                Directory.CreateDirectory(Path.Combine(wd, ".git"));
                File.WriteAllText(Path.Combine(wd, ".git", "HEAD"), "ref: refs/heads/main");
            },
        };

        var result = new RestoreGitSource(runner, _staging).Materialize(
            Target, "1111111111111111111111111111111111aaaa", _dest);

        Assert.True(result.Ok);
        Assert.Equal(_dest, result.StagedRoot);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dest, "settings.json")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dest, "commands", "a.md")));
        Assert.False(Directory.Exists(Path.Combine(_dest, ".git")));
    }

    // Restore rule 4: a denylisted file checked out as part of a (tampered
    // or otherwise unexpected) commit must never reach the materialised
    // staging folder, even though GitBackend itself never stages one.
    [Fact]
    public void MaterializeRefusesToCopyADenylistedCheckedOutFile()
    {
        var runner = new FakeRunner
        {
            OnCheckout = wd =>
            {
                File.WriteAllText(Path.Combine(wd, "settings.json"), "{}");
                File.WriteAllText(Path.Combine(wd, ".credentials.json"), "secret");
            },
        };

        var result = new RestoreGitSource(runner, _staging).Materialize(
            Target, "1111111111111111111111111111111111aaaa", _dest);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dest, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_dest, ".credentials.json")));
    }

    [Fact]
    public void MaterializeUsesDetachedForcedCheckoutAndNeverTouchesABranch()
    {
        var runner = new FakeRunner();
        new RestoreGitSource(runner, _staging).Materialize(Target, "1111111111111111111111111111111111aaaa", _dest);

        Assert.Contains(runner.Calls,
            c => c == "git checkout --force --detach 1111111111111111111111111111111111aaaa");
    }
}

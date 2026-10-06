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

    // Stands in for the live config root ("~/.claude") in tests - see
    // RestoreZipSourceTests._protected for the same reasoning.
    private readonly string _protected = Path.Combine(Path.GetTempPath(), $"rgsprotected-{Guid.NewGuid():N}");

    private const string ShaA = "1111111111111111111111111111111111aaaa";
    private const string ShaB = "2222222222222222222222222222222222bbbb";

    public void Dispose()
    {
        if (Directory.Exists(_staging)) Directory.Delete(_staging, true);
        if (Directory.Exists(_dest)) Directory.Delete(_dest, true);
        if (Directory.Exists(_protected)) Directory.Delete(_protected, true);
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
        var older = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

        // Deliberately out of chronological order in the fixture text, to
        // prove the parser sorts rather than trusting input order.
        // matches the %x1f unit separator RestoreGitSource asks `git log`
        // for - see RestoreGitSource.LogFormat.
        var logOutput =
            $"{ShaA}\u001f{older:o}\u001fBackup 2026-01-01 09:00:00\n" +
            $"{ShaB}\u001f{newer:o}\u001fBackup 2026-02-01 09:00:00\n";

        var runner = new FakeRunner { LogStdOut = logOutput };
        var result = new RestoreGitSource(runner, _staging).ListSnapshots(Target);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Snapshots.Count);
        Assert.Equal(ShaB, result.Snapshots[0].Id);
        Assert.Equal(ShaA, result.Snapshots[1].Id);
        Assert.Contains(ShaB[..7], result.Snapshots[0].DisplayName);
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
        var result = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _dest, _protected);

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
        var result = new RestoreGitSource(runner, _staging).Materialize(Target, snapshotId, _dest, _protected);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git checkout"));
    }

    [Fact]
    public void MaterializeFailsCleanlyWhenCheckoutFails()
    {
        var runner = new FakeRunner { CheckoutShouldFail = true, CheckoutStdErr = "fatal: reference is not a tree" };
        var result = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _dest, _protected);

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

        var result = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _dest, _protected);

        Assert.True(result.Ok);
        Assert.Equal(_dest, result.StagedRoot);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dest, "settings.json")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dest, "commands", "a.md")));
        Assert.False(Directory.Exists(Path.Combine(_dest, ".git")));
    }

    // Fix round 1, Important 1: materialising commit A and then, later,
    // commit B into the SAME destinationDir must leave exactly B's files -
    // not the union of A and B (see RestoreZipSourceTests' Drive equivalent
    // for the full reasoning).
    [Fact]
    public void MaterializingASecondCommitIntoTheSameDestinationReplacesTheFirstEntirely()
    {
        var runner = new FakeRunner
        {
            OnCheckout = wd =>
            {
                File.WriteAllText(Path.Combine(wd, "settings.json"), "from A");
                File.WriteAllText(Path.Combine(wd, "only-in-a.txt"), "only A has this");
            },
        };
        var first = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _dest, _protected);
        Assert.True(first.Ok);
        Assert.True(File.Exists(Path.Combine(_dest, "only-in-a.txt")));

        runner.OnCheckout = wd =>
        {
            // A real `git checkout --force` of a different commit removes
            // files the previous commit had that the new one does not - the
            // fake simulates exactly that end state (only-in-a.txt gone).
            File.Delete(Path.Combine(wd, "only-in-a.txt"));
            File.WriteAllText(Path.Combine(wd, "settings.json"), "from B");
        };
        var second = new RestoreGitSource(runner, _staging).Materialize(Target, ShaB, _dest, _protected);

        Assert.True(second.Ok);
        Assert.Equal("from B", File.ReadAllText(Path.Combine(_dest, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_dest, "only-in-a.txt")));
        Assert.Equal(
            new[] { Path.Combine(_dest, "settings.json") },
            Directory.GetFiles(_dest, "*", SearchOption.AllDirectories));
    }

    // Fix round 1, Important 3: Materialize must refuse to write into the
    // live config root, whichever direction the overlap runs.
    [Fact]
    public void MaterializeRefusesADestinationEqualToTheProtectedRoot()
    {
        var runner = new FakeRunner();
        var result = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _protected, _protected);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git checkout"));
    }

    [Fact]
    public void MaterializeRefusesADestinationNestedInsideTheProtectedRoot()
    {
        var nested = Path.Combine(_protected, "staging");
        var runner = new FakeRunner();
        var result = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, nested, _protected);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git checkout"));
    }

    [Fact]
    public void MaterializeRefusesADestinationThatContainsTheProtectedRoot()
    {
        var protectedInsideDest = Path.Combine(_dest, "live-root");
        var runner = new FakeRunner();
        var result = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _dest, protectedInsideDest);

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Calls, c => c.StartsWith("git checkout"));
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

        var result = new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _dest, _protected);

        Assert.True(result.Ok);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dest, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_dest, ".credentials.json")));
    }

    [Fact]
    public void MaterializeUsesDetachedForcedCheckoutAndNeverTouchesABranch()
    {
        var runner = new FakeRunner();
        new RestoreGitSource(runner, _staging).Materialize(Target, ShaA, _dest, _protected);

        Assert.Contains(runner.Calls, c => c == $"git checkout --force --detach {ShaA}");
    }
}

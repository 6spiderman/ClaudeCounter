// tests/ClaudeCounter.Tests/RestoreTests/RestoreApplierTests.cs
using ClaudeBackup;
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

public class RestoreApplierTests : IDisposable
{
    private readonly string _staged = Path.Combine(Path.GetTempPath(), $"rastg-{Guid.NewGuid():N}");
    private readonly string _live = Path.Combine(Path.GetTempPath(), $"ralive-{Guid.NewGuid():N}");
    private readonly string _safetyBase = Path.Combine(Path.GetTempPath(), $"rasafety-{Guid.NewGuid():N}");

    public RestoreApplierTests()
    {
        Directory.CreateDirectory(_staged);
        Directory.CreateDirectory(_live);
    }

    public void Dispose()
    {
        if (Directory.Exists(_staged)) Directory.Delete(_staged, true);
        if (Directory.Exists(_live)) Directory.Delete(_live, true);
        if (Directory.Exists(_safetyBase)) Directory.Delete(_safetyBase, true);
    }

    private void Stage(string rel, string content)
    {
        var path = Path.Combine(_staged, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Live(string rel, string content)
    {
        var path = Path.Combine(_live, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

    // Restore rule 2: applying takes a safety copy of exactly the files it
    // overwrites, and NOT of files it merely creates.
    [Fact]
    public void SafetyCopyCoversOnlyOverwrittenFilesNotCreatedOnes()
    {
        Stage("new.txt", "new content");
        Stage("changed.txt", "new version");
        Live("changed.txt", "old version");

        var chosen = new[]
        {
            new RestoreFileEntry("new.txt", RestoreFileStatus.New, 11, null, null, null),
            new RestoreFileEntry("changed.txt", RestoreFileStatus.Changed, 11, null, 11, null),
        };

        var result = RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);

        Assert.True(result.Ok);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.OverwrittenCount);
        Assert.NotNull(result.SafetyCopyPath);

        // The overwritten file's ORIGINAL (live) content is what got saved,
        // preserving its relative path.
        var safetyFile = Path.Combine(result.SafetyCopyPath!, "changed.txt");
        Assert.True(File.Exists(safetyFile));
        Assert.Equal("old version", File.ReadAllText(safetyFile));

        // The created file has no safety copy - there was nothing to lose.
        Assert.False(File.Exists(Path.Combine(result.SafetyCopyPath!, "new.txt")));

        // Live tree reflects the restore.
        Assert.Equal("new content", File.ReadAllText(Path.Combine(_live, "new.txt")));
        Assert.Equal("new version", File.ReadAllText(Path.Combine(_live, "changed.txt")));
    }

    [Fact]
    public void NoOverwritesMeansNoSafetyFolderAtAll()
    {
        Stage("new.txt", "new content");
        var chosen = new[] { new RestoreFileEntry("new.txt", RestoreFileStatus.New, 11, null, null, null) };

        var result = RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);

        Assert.True(result.Ok);
        Assert.Null(result.SafetyCopyPath);
        Assert.False(Directory.Exists(Path.Combine(_safetyBase, "20260810-090000")));
    }

    // Restore rule 3: a LiveOnly entry is never in the apply set, even if a
    // caller (a future dialog bug) somehow passes one in - and the live file
    // it refers to is left completely alone.
    [Fact]
    public void LiveOnlyEntryIsNeverAppliedEvenIfPassedIn()
    {
        Live("orphan.txt", "must survive");
        var chosen = new[]
        {
            new RestoreFileEntry("orphan.txt", RestoreFileStatus.LiveOnly, null, null, 12, null),
        };

        var result = RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);

        Assert.True(result.Ok);
        Assert.Empty(result.WrittenPaths);
        Assert.Contains("orphan.txt", result.SkippedPaths);
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(_live, "orphan.txt")));
        Assert.Null(result.SafetyCopyPath);
    }

    [Fact]
    public void IdenticalEntryIsNeverApplied()
    {
        Stage("same.txt", "same");
        Live("same.txt", "same");
        var chosen = new[]
        {
            new RestoreFileEntry("same.txt", RestoreFileStatus.Identical, 4, null, 4, null),
        };

        var result = RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);

        Assert.True(result.Ok);
        Assert.Empty(result.WrittenPaths);
        Assert.Contains("same.txt", result.SkippedPaths);
    }

    // Restore rule 4: even a caller that bypasses materialisation entirely
    // (constructs a RestoreFileEntry directly, as this test does) must never
    // get a denylisted file written into live ~/.claude. This is the
    // point-of-write backstop, independent of RestoreZipSource/RestoreGitSource
    // already filtering on the way in.
    [Fact]
    public void DenylistedPathIsRefusedEvenIfPassedInDirectly()
    {
        Stage(".credentials.json", "secret");
        var chosen = new[]
        {
            new RestoreFileEntry(".credentials.json", RestoreFileStatus.New, 6, null, null, null),
        };

        var result = RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);

        Assert.True(result.Ok);
        Assert.Empty(result.WrittenPaths);
        Assert.Contains(".credentials.json", result.SkippedPaths);
        Assert.False(File.Exists(Path.Combine(_live, ".credentials.json")));
    }

    // Restore rule 5: a path that would resolve outside liveRoot must never
    // be written, however it reached Apply. Caught by RelativePathGuard.IsSafe
    // (the ".." segment check) - the second, full-path containment layer
    // (RelativePathGuard.IsWithinDirectory) is exercised directly by
    // GitBackendTests.IsWithinDirectoryDetectsEscapes, which still applies
    // unchanged now that the check lives in RelativePathGuard.
    [Fact]
    public void PathEscapingLiveRootIsRefused()
    {
        var chosen = new[]
        {
            new RestoreFileEntry("../evil.json", RestoreFileStatus.New, 10, null, null, null),
        };

        var result = RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);

        Assert.True(result.Ok);
        Assert.Empty(result.WrittenPaths);
        Assert.Contains("../evil.json", result.SkippedPaths);
        var escapedTarget = Path.Combine(Path.GetDirectoryName(_live)!, "evil.json");
        Assert.False(File.Exists(escapedTarget));
    }

    [Fact]
    public void StagedFileMissingAtApplyTimeIsSkippedNotThrown()
    {
        // Entry claims a staged file that does not actually exist on disk -
        // simulates a race between preview and apply.
        var chosen = new[]
        {
            new RestoreFileEntry("gone.txt", RestoreFileStatus.New, 5, null, null, null),
        };

        var result = RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);

        Assert.True(result.Ok);
        Assert.Contains("gone.txt", result.SkippedPaths);
    }

    // Every applied path is logged (design spec: "log every path written").
    [Fact]
    public void EveryWrittenPathIsLogged()
    {
        Stage("settings.json", "{}");
        var chosen = new[] { new RestoreFileEntry("settings.json", RestoreFileStatus.New, 2, null, null, null) };

        var before = ReadLog().Length;
        RestoreApplier.Apply(_staged, _live, chosen, _safetyBase, FixedNow);
        var written = ReadLog()[before..];

        Assert.Contains("settings.json", written);
    }

    private static string ReadLog()
    {
        if (Log.FilePath is not { } path || !File.Exists(path))
            return string.Empty;

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

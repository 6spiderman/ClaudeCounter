// tests/ClaudeCounter.Tests/RestoreTests/RestoreClassifierTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

public class RestoreClassifierTests : IDisposable
{
    private readonly string _staged = Path.Combine(Path.GetTempPath(), $"rcstg-{Guid.NewGuid():N}");
    private readonly string _live = Path.Combine(Path.GetTempPath(), $"rclive-{Guid.NewGuid():N}");

    public RestoreClassifierTests()
    {
        Directory.CreateDirectory(_staged);
        Directory.CreateDirectory(_live);
    }

    public void Dispose()
    {
        if (Directory.Exists(_staged)) Directory.Delete(_staged, true);
        if (Directory.Exists(_live)) Directory.Delete(_live, true);
    }

    private void Stage(string rel, string content)
    {
        var path = Path.Combine(_staged, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Live(string rel, string content)
    {
        var path = Path.Combine(_live, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private RestoreFileEntry Find(IReadOnlyList<RestoreFileEntry> entries, string rel) =>
        Assert.Single(entries, e => e.RelativePath == rel);

    [Fact]
    public void FileAbsentLiveIsClassifiedNew()
    {
        Stage("settings.json", "{}");

        var entries = RestoreClassifier.Classify(_staged, _live);

        var entry = Find(entries, "settings.json");
        Assert.Equal(RestoreFileStatus.New, entry.Status);
        Assert.Equal(2, entry.StagedSizeBytes);
        Assert.Null(entry.LiveSizeBytes);
    }

    // Restore rule 3, made visible at the classification layer: a file
    // present live but absent from the backup is reported as LiveOnly, not
    // silently dropped and not treated as anything that would ever be
    // deleted.
    [Fact]
    public void FileAbsentFromBackupIsClassifiedLiveOnly()
    {
        Live("orphan.txt", "still here");

        var entries = RestoreClassifier.Classify(_staged, _live);

        var entry = Find(entries, "orphan.txt");
        Assert.Equal(RestoreFileStatus.LiveOnly, entry.Status);
        Assert.Null(entry.StagedSizeBytes);
        Assert.NotNull(entry.LiveSizeBytes);
    }

    [Fact]
    public void ByteIdenticalFileIsClassifiedIdentical()
    {
        Stage("CLAUDE.md", "same content");
        Live("CLAUDE.md", "same content");

        var entries = RestoreClassifier.Classify(_staged, _live);

        Assert.Equal(RestoreFileStatus.Identical, Find(entries, "CLAUDE.md").Status);
    }

    [Fact]
    public void DifferentLengthFileIsClassifiedChanged()
    {
        Stage("settings.json", "a");
        Live("settings.json", "bb");

        var entries = RestoreClassifier.Classify(_staged, _live);

        var entry = Find(entries, "settings.json");
        Assert.Equal(RestoreFileStatus.Changed, entry.Status);
        Assert.Equal(1, entry.StagedSizeBytes);
        Assert.Equal(2, entry.LiveSizeBytes);
    }

    // The case a naive length-only comparison gets wrong: both files are
    // exactly 4 bytes, but the content differs. The design spec calls for
    // hashing only when lengths already match - this is the test that
    // proves that optimisation did not quietly turn into "same length means
    // identical".
    [Fact]
    public void SameLengthDifferentContentIsClassifiedChangedNotIdentical()
    {
        Stage("settings.json", "abcd");
        Live("settings.json", "abce");

        var entries = RestoreClassifier.Classify(_staged, _live);

        Assert.Equal(RestoreFileStatus.Changed, Find(entries, "settings.json").Status);
    }

    [Fact]
    public void ClassifiesAMixOfAllFourStatusesInOneRun()
    {
        Stage("new.txt", "new");
        Stage("changed.txt", "staged-version");
        Live("changed.txt", "live-version!!!"); // different length too
        Stage("same.txt", "identical");
        Live("same.txt", "identical");
        Live("orphan.txt", "left alone");

        var entries = RestoreClassifier.Classify(_staged, _live);

        Assert.Equal(4, entries.Count);
        Assert.Equal(RestoreFileStatus.New, Find(entries, "new.txt").Status);
        Assert.Equal(RestoreFileStatus.Changed, Find(entries, "changed.txt").Status);
        Assert.Equal(RestoreFileStatus.Identical, Find(entries, "same.txt").Status);
        Assert.Equal(RestoreFileStatus.LiveOnly, Find(entries, "orphan.txt").Status);
    }

    // A fresh machine's live ~/.claude may not exist at all yet - Classify
    // must treat that as "no live files", not throw.
    [Fact]
    public void MissingLiveRootTreatsEverythingAsNew()
    {
        Stage("settings.json", "{}");
        var missingLive = Path.Combine(Path.GetTempPath(), $"rclive-missing-{Guid.NewGuid():N}");

        var entries = RestoreClassifier.Classify(_staged, missingLive);

        Assert.Equal(RestoreFileStatus.New, Find(entries, "settings.json").Status);
    }

    [Fact]
    public void MissingStagedRootTreatsEverythingAsLiveOnly()
    {
        Live("settings.json", "{}");
        var missingStaged = Path.Combine(Path.GetTempPath(), $"rcstg-missing-{Guid.NewGuid():N}");

        var entries = RestoreClassifier.Classify(missingStaged, _live);

        Assert.Equal(RestoreFileStatus.LiveOnly, Find(entries, "settings.json").Status);
    }

    // A .git directory should never appear in an already-materialised
    // staging folder (RestoreGitSource copies files OUT of its worktree
    // before returning), but Classify defends against that invariant
    // slipping anyway rather than reporting git's own internal files as
    // restorable content.
    [Fact]
    public void IgnoresADotGitDirectoryUnderStagedRoot()
    {
        Stage("settings.json", "{}");
        Directory.CreateDirectory(Path.Combine(_staged, ".git"));
        File.WriteAllText(Path.Combine(_staged, ".git", "HEAD"), "ref: refs/heads/main");

        var entries = RestoreClassifier.Classify(_staged, _live);

        Assert.DoesNotContain(entries, e => e.RelativePath.Contains(".git"));
    }
}

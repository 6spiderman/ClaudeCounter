// tests/ClaudeCounter.Tests/ClaudeBackupTests/ClaudeLocationScannerTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class ClaudeLocationScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cls-{Guid.NewGuid():N}");

    public ClaudeLocationScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [Fact]
    public void ProbeCandidatesReturnsOnlyExistingPaths()
    {
        var existingDir = Path.Combine(_root, "exists-dir");
        Directory.CreateDirectory(existingDir);
        var existingFile = Path.Combine(_root, "exists.json");
        File.WriteAllText(existingFile, "{}");
        var missingDir = Path.Combine(_root, "missing-dir");
        var missingFile = Path.Combine(_root, "missing.json");

        var candidates = new[]
        {
            new ClaudeLocation("Existing dir", existingDir, true),
            new ClaudeLocation("Existing file", existingFile, false),
            new ClaudeLocation("Missing dir", missingDir, true),
            new ClaudeLocation("Missing file", missingFile, false),
        };

        var found = ClaudeLocationScanner.ProbeCandidates(candidates);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, l => l.Path == existingDir);
        Assert.Contains(found, l => l.Path == existingFile);
    }

    [Fact]
    public void ProbeCandidatesToleratesAWildlyInvalidPathWithoutThrowing()
    {
        // A path shape the OS itself will reject (invalid characters for
        // NTFS) - stands in for "candidate cannot be probed" the same way an
        // access-denied directory could not be probed. Must be treated as
        // simply absent, not propagate an exception that would take down
        // every other candidate's probe with it.
        var invalid = new ClaudeLocation("Invalid", "\0invalid\0path", true);
        var validDir = Path.Combine(_root, "ok-dir");
        Directory.CreateDirectory(validDir);
        var valid = new ClaudeLocation("Valid", validDir, true);

        var found = ClaudeLocationScanner.ProbeCandidates(new[] { invalid, valid });

        Assert.Single(found);
        Assert.Equal(validDir, found[0].Path);
    }

    [Fact]
    public void ProbeBuildsCandidatesFromRealKnownFolders()
    {
        // Not asserting on existence (machine-dependent) - just that the
        // real, parameterless entry point builds a fixed, non-empty
        // candidate list and never throws building or probing it.
        var candidates = ClaudeLocationScanner.KnownCandidates();
        Assert.NotEmpty(candidates);

        var probed = ClaudeLocationScanner.Probe();
        Assert.All(probed, l => Assert.True(
            l.IsDirectory ? Directory.Exists(l.Path) : File.Exists(l.Path)));
    }

    [Fact]
    public void ComputeStatsCountsFilesAndBytesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "1234");       // 4 bytes
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "12"); // 2 bytes

        var stats = ClaudeLocationScanner.ComputeStats(_root, isDirectory: true);

        Assert.Equal(2, stats.FileCount);
        Assert.Equal(6, stats.TotalBytes);
    }

    [Fact]
    public void ComputeStatsForAFileIsASingleFileImmediately()
    {
        var file = Path.Combine(_root, "solo.txt");
        File.WriteAllText(file, "12345");

        var stats = ClaudeLocationScanner.ComputeStats(file, isDirectory: false);

        Assert.Equal(1, stats.FileCount);
        Assert.Equal(5, stats.TotalBytes);
    }

    [Fact]
    public void ComputeStatsOnMissingPathReturnsZeroWithoutThrowing()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var dirStats = ClaudeLocationScanner.ComputeStats(missing, isDirectory: true);
        var fileStats = ClaudeLocationScanner.ComputeStats(missing, isDirectory: false);

        Assert.Equal(0, dirStats.FileCount);
        Assert.Equal(0, dirStats.TotalBytes);
        Assert.Equal(0, fileStats.FileCount);
        Assert.Equal(0, fileStats.TotalBytes);
    }

    [Fact]
    public void ComputeStatsSkipsAnUnreadableSubdirectoryAndKeepsGoing()
    {
        // Stand-in for an access-denied subdirectory: a path that exists as
        // a FILE where ComputeStats's own recursive walk expects a
        // directory. Directory.GetDirectories/GetFiles on it throws
        // IOException, which is exactly the branch this test proves does
        // not abort the whole count - it is caught and enumeration
        // continues with the tree's other, readable entries.
        var goodSub = Path.Combine(_root, "good");
        Directory.CreateDirectory(goodSub);
        File.WriteAllText(Path.Combine(goodSub, "ok.txt"), "12");

        var stats = ClaudeLocationScanner.ComputeStats(Path.Combine(_root, "good", "ok.txt"), isDirectory: true);

        // The "directory" we pointed at is actually a file, so the walk's
        // very first GetDirectories/GetFiles call throws and is swallowed -
        // proving the swallow path is reachable without ever leaving a
        // partially-summed count from a sibling.
        Assert.Equal(0, stats.FileCount);
        Assert.Equal(0, stats.TotalBytes);
    }
}

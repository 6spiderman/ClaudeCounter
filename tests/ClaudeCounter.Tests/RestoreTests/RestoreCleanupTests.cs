// tests/ClaudeCounter.Tests/RestoreTests/RestoreCleanupTests.cs
using ClaudeBackup;
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

public class RestoreCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rclean-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void DeletesAnExistingDirectoryRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "content");
        File.WriteAllText(Path.Combine(_dir, "sub", "b.txt"), "content");

        RestoreCleanup.DeleteStagingDirectory(_dir);

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void MissingDirectoryIsANoOpNotAThrow()
    {
        var exception = Record.Exception(() => RestoreCleanup.DeleteStagingDirectory(_dir));

        Assert.Null(exception);
        Assert.False(Directory.Exists(_dir));
    }

    // A cleanup failure (a file locked by an AV scanner/indexer, or held
    // open by something else) must be logged loudly, not thrown and not
    // silently swallowed - the directory can hold a plaintext copy of a
    // subset of the user's Claude config.
    [Fact]
    public void LockedFileIsLoggedLoudlyAndDoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        var lockedFile = Path.Combine(_dir, "locked.txt");
        File.WriteAllText(lockedFile, "content");

        using var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        var before = ReadLog().Length;
        var exception = Record.Exception(() => RestoreCleanup.DeleteStagingDirectory(_dir));
        var written = ReadLog()[before..];

        Assert.Null(exception);
        Assert.Contains("failed to delete staging directory", written);
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

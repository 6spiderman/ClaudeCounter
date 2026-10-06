// tests/ClaudeCounter.Tests/RestoreTests/RelativePathGuardTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

// Fix round 1, Important 4: RelativePathGuard.IsSafe was previously
// exercised only end to end, via a "../escape.json" zip entry - the
// absolute (C:\evil.json), rooted (/evil.json), and backslash (..\evil.json)
// rejections were enforced but unproven, so an edit dropping the
// IsPathRooted or Contains('\\') check would have broken zero tests. These
// cover all four shapes directly, plus RelativePathGuard.Overlaps (added in
// the same fix round for restore rule 1 / Important 3).
public class RelativePathGuardTests
{
    [Theory]
    [InlineData("settings.json")]
    [InlineData("CLAUDE.md")]
    [InlineData("commands/a.md")]
    [InlineData("a/b/c.txt")]
    [InlineData("plugins/my-server/mcp.json")]
    public void IsSafeAllowsLegitimateRelativePaths(string path) => Assert.True(RelativePathGuard.IsSafe(path));

    [Theory]
    [InlineData("../evil.json")]              // parent-directory traversal
    [InlineData("a/../../evil.json")]         // traversal past the root via a nested ".."
    [InlineData("./evil.json")]               // "." segment
    [InlineData(@"C:\evil.json")]             // absolute, drive-rooted (Windows)
    [InlineData("/evil.json")]                // rooted (leading separator)
    [InlineData(@"a\evil.json")]              // backslash instead of forward slash
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void IsSafeRejectsUnsafePaths(string path) => Assert.False(RelativePathGuard.IsSafe(path));

    [Fact]
    public void OverlapsIsTrueForIdenticalDirectories()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rpg-{Guid.NewGuid():N}");
        Assert.True(RelativePathGuard.Overlaps(dir, dir));
    }

    [Fact]
    public void OverlapsIsTrueWhenFirstIsNestedInsideSecond()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"rpg-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "sub");
        Assert.True(RelativePathGuard.Overlaps(child, parent));
    }

    [Fact]
    public void OverlapsIsTrueWhenSecondIsNestedInsideFirst()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"rpg-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "sub");
        Assert.True(RelativePathGuard.Overlaps(parent, child));
    }

    [Fact]
    public void OverlapsIsFalseForUnrelatedDirectories()
    {
        var a = Path.Combine(Path.GetTempPath(), $"rpg-a-{Guid.NewGuid():N}");
        var b = Path.Combine(Path.GetTempPath(), $"rpg-b-{Guid.NewGuid():N}");
        Assert.False(RelativePathGuard.Overlaps(a, b));
    }

    // The classic prefix-collision case IsWithinDirectory already guards
    // against - Overlaps must not inherit a false positive from a naive
    // StartsWith either.
    [Fact]
    public void OverlapsIsFalseForASiblingWithASharedNamePrefix()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"rpg-base-{Guid.NewGuid():N}");
        var sibling = baseDir + "-evil";
        Assert.False(RelativePathGuard.Overlaps(baseDir, sibling));
    }
}

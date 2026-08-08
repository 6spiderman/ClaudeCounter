// tests/ClaudeCounter.Tests/ClaudeBackupTests/FileSelectorTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class FileSelectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fsel-{Guid.NewGuid():N}");

    public FileSelectorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "commands"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "x"));
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        File.WriteAllText(Path.Combine(_root, "commands", "a.md"), "a");
        File.WriteAllText(Path.Combine(_root, "projects", "x", "big.log"), "log");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void IncludesMatchesAndExcludesProjectsAndSecrets()
    {
        var sel = new FileSelector();
        var result = sel.Select(_root,
            new[] { "settings.json", "commands/**", ".credentials.json" },
            new[] { "projects/**" });

        Assert.Contains("settings.json", result);
        Assert.Contains("commands/a.md", result);
        Assert.DoesNotContain("projects/x/big.log", result); // excluded
        Assert.DoesNotContain(".credentials.json", result);   // denylisted even if included
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "SETTINGS.JSON" }, Array.Empty<string>());

        Assert.Contains("settings.json", result);
    }

    [Fact]
    public void NoIncludePatternsSelectsNothing()
    {
        var sel = new FileSelector();
        var result = sel.Select(_root, Array.Empty<string>(), Array.Empty<string>());

        Assert.Empty(result);
    }

    // Mirrors BackupConfig.Default().Include's "plugins/**/*.json": the glob
    // must reach both a plugin file directly under "plugins/" and one nested
    // under a per-plugin subdirectory, while the denylist still strips
    // anything secret-shaped that the glob picked up.
    [Fact]
    public void PluginJsonGlobIncludesNestedFilesButDenylistStripsSecretShapedOnes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "my-server"));
        File.WriteAllText(Path.Combine(_root, "plugins", "top.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "plugins", "my-server", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "plugins", "my-server", "api_token.json"), "{\"token\":\"x\"}");

        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "plugins/**/*.json" }, Array.Empty<string>());

        Assert.Contains("plugins/top.json", result);
        Assert.Contains("plugins/my-server/manifest.json", result);
        Assert.DoesNotContain("plugins/my-server/api_token.json", result);
    }
}

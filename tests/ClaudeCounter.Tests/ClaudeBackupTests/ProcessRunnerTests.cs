// tests/ClaudeCounter.Tests/ClaudeBackupTests/ProcessRunnerTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class ProcessRunnerTests
{
    [Fact]
    public void RunsAndCapturesExitCode()
    {
        var runner = new ProcessRunner();
        // `cmd /c exit 3` is available on the Windows CI runner.
        var result = runner.Run("cmd", new[] { "/c", "exit", "3" });
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Ok);
    }

    [Fact]
    public void ExistsFindsCmd() => Assert.True(new ProcessRunner().Exists("cmd"));

    [Fact]
    public void ExistsFalseForNonsense() =>
        Assert.False(new ProcessRunner().Exists("definitely-not-a-real-binary-xyz"));
}

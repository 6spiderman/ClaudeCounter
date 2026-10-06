using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class ProcessRunnerTests
{
    // The platform's own shell: cmd on Windows, sh everywhere else.
    private static (string Shell, string[] ExitThree) Shell => OperatingSystem.IsWindows()
        ? ("cmd", new[] { "/c", "exit", "3" })
        : ("sh", new[] { "-c", "exit 3" });

    [Fact]
    public void RunsAndCapturesExitCode()
    {
        var runner = new ProcessRunner();
        var result = runner.Run(Shell.Shell, Shell.ExitThree);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Ok);
    }

    [Fact]
    public void ExistsFindsTheShell() => Assert.True(new ProcessRunner().Exists(Shell.Shell));

    [Fact]
    public void ExistsFalseForNonsense() =>
        Assert.False(new ProcessRunner().Exists("definitely-not-a-real-binary-xyz"));
}

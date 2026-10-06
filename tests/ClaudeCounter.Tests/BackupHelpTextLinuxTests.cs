using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// The Linux help text is the Windows guide with a few passages swapped. If
/// the Windows wording of a swapped passage is ever edited, the swap silently
/// stops applying - these tests catch that.
/// </summary>
public class BackupHelpTextLinuxTests
{
    [Fact]
    public void EverySwapStillFindsItsWindowsPassage()
    {
        foreach (var (windows, _) in BackupHelpText.LinuxSwaps)
            Assert.Contains(windows, BackupHelpText.FullGuide, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Task Scheduler")]
    [InlineData("UNC")]
    [InlineData("drive letter")]
    [InlineData("Google Drive for Desktop")]
    public void LinuxGuideHasNoWindowsOnlyInstructions(string windowsOnly)
    {
        Assert.DoesNotContain(windowsOnly, BackupHelpText.FullGuideLinux, StringComparison.Ordinal);
        Assert.DoesNotContain(windowsOnly, BackupHelpText.SyncFolderLinux, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxGuideDescribesTheSystemdTimer()
    {
        Assert.Contains("systemd timer", BackupHelpText.FullGuideLinux, StringComparison.Ordinal);
        Assert.Contains("claudecounter-backup.timer", BackupHelpText.FullGuideLinux, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsGuideIsUnchanged()
    {
        Assert.Contains("Task Scheduler", BackupHelpText.FullGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("systemd", BackupHelpText.FullGuide, StringComparison.Ordinal);
    }
}

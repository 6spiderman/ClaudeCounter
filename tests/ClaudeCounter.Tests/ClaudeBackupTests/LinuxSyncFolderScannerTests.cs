using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.ClaudeBackupTests;

/// <summary>
/// SyncFolderScanner.DetectLinuxFrom / ParseLinuxMounts - pure, so they run
/// in the Windows CI job like every other test, against fake folder listings.
/// </summary>
public class LinuxSyncFolderScannerTests
{
    private const string Home = "/home/u";

    private static IReadOnlyList<SyncFolderCandidate> Detect(
        Dictionary<string, string[]> folders,
        IReadOnlyList<(string, string, string)>? mounts = null,
        string? gvfs = null) =>
        SyncFolderScanner.DetectLinuxFrom(
            Home,
            mounts ?? Array.Empty<(string, string, string)>(),
            gvfs,
            path => folders.TryGetValue(path, out var children) ? children : Array.Empty<string>(),
            path => folders.ContainsKey(path));

    [Fact]
    public void FindsTheDefaultClientFolders()
    {
        var found = Detect(new()
        {
            [Home] = new[] { "Dropbox", "OneDrive", "Documents", "Google Drive" },
            ["/home/u/Dropbox"] = Array.Empty<string>(),
            ["/home/u/OneDrive"] = Array.Empty<string>(),
            ["/home/u/Google Drive"] = Array.Empty<string>(),
        });

        Assert.Contains(found, c => c.DisplayName == "Dropbox" && c.Path == "/home/u/Dropbox");
        Assert.Contains(found, c => c.DisplayName == "OneDrive" && c.Path == "/home/u/OneDrive");
        Assert.Contains(found, c => c.DisplayName == "Google Drive (~/Google Drive)");
        Assert.DoesNotContain(found, c => c.Path.EndsWith("Documents", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsInsyncAccountsAndSecondaryAccounts()
    {
        var found = Detect(new()
        {
            [Home] = new[] { "Insync", "Dropbox (Personal)", "OneDrive - Contoso" },
            ["/home/u/Insync"] = new[] { "me@example.com" },
            ["/home/u/Insync/me@example.com/Google Drive"] = Array.Empty<string>(),
            ["/home/u/Dropbox (Personal)"] = Array.Empty<string>(),
            ["/home/u/OneDrive - Contoso"] = Array.Empty<string>(),
        });

        Assert.Contains(found, c => c.DisplayName == "Google Drive (Insync: me@example.com)");
        Assert.Contains(found, c => c.DisplayName == "Dropbox (Personal)");
        Assert.Contains(found, c => c.DisplayName == "OneDrive - Contoso");
    }

    [Fact]
    public void OffersOnlyNetworkMountsThatAreReachable()
    {
        var found = Detect(
            new() { [Home] = Array.Empty<string>(), ["/mnt/nas"] = Array.Empty<string>() },
            new[]
            {
                ("//nas/media", "/mnt/nas", "cifs"),
                ("//nas/offline", "/mnt/offline", "cifs"), // not reachable: not in the folder map
                ("/dev/sda1", "/", "ext4"),                // not a network file system
            });

        var nas = Assert.Single(found);
        Assert.Equal("NAS share (//nas/media)", nas.DisplayName);
        Assert.Equal("/mnt/nas", nas.Path);
    }

    [Fact]
    public void FindsSharesMountedByTheFileManager()
    {
        const string gvfs = "/run/user/1000/gvfs";
        var share = gvfs + "/smb-share:server=nas,share=media";
        var found = Detect(
            new()
            {
                [Home] = Array.Empty<string>(),
                [gvfs] = new[] { "smb-share:server=nas,share=media", "mtp:host=phone" },
                [share] = Array.Empty<string>(),
                [gvfs + "/mtp:host=phone"] = Array.Empty<string>(),
            },
            gvfs: gvfs);

        var nas = Assert.Single(found);
        Assert.StartsWith("NAS share", nas.DisplayName, StringComparison.Ordinal);
        Assert.Equal(share, nas.Path);
    }

    [Fact]
    public void ProviderPrefixesMatchTheWindowsCandidates()
    {
        var found = Detect(new()
        {
            [Home] = new[] { "Dropbox", "OneDrive", "gdrive" },
            ["/home/u/Dropbox"] = Array.Empty<string>(),
            ["/home/u/OneDrive"] = Array.Empty<string>(),
            ["/home/u/gdrive"] = Array.Empty<string>(),
        });

        Assert.Single(SyncFolderScanner.FilterByProvider(found, SyncProvider.Dropbox));
        Assert.Single(SyncFolderScanner.FilterByProvider(found, SyncProvider.OneDrive));
        Assert.Single(SyncFolderScanner.FilterByProvider(found, SyncProvider.GoogleDrive));
    }

    [Fact]
    public void ParsesEscapedSpacesInMountsTable()
    {
        var mounts = SyncFolderScanner.ParseLinuxMounts(new[]
        {
            @"//nas/My\040Share /mnt/my\040share cifs rw 0 0",
            "short line",
        });

        var mount = Assert.Single(mounts);
        Assert.Equal("//nas/My Share", mount.Source);
        Assert.Equal("/mnt/my share", mount.MountPoint);
        Assert.Equal("cifs", mount.FileSystem);
    }

    [Theory]
    [InlineData("/run/user/1000/gvfs/smb-share:server=nas,share=media/claude", SyncProvider.Nas)]
    [InlineData("/home/u/Dropbox/claude", SyncProvider.Dropbox)]
    [InlineData("/home/u/OneDrive/claude", SyncProvider.OneDrive)]
    public void InfersLinuxProviders(string path, SyncProvider expected) =>
        Assert.Equal(expected, SyncProviderInference.InferFromPath(path));
}

/// <summary>ProcessRunner.ExistsOnPath - how git/rclone are found on Linux.</summary>
public class ProcessRunnerExistsOnPathTests
{
    private static readonly HashSet<string> Files = new(StringComparer.Ordinal) { "/usr/bin/git", "/home/u/bin/rclone" };

    [Theory]
    [InlineData("git", true)]
    [InlineData("rclone", true)]
    [InlineData("svn", false)]
    [InlineData("/usr/bin/git", true)]
    [InlineData("/opt/git", false)]
    [InlineData("", false)]
    public void LooksUpBareNamesOnPathAndChecksPathsDirectly(string file, bool expected) =>
        Assert.Equal(expected, ProcessRunner.ExistsOnPath(file, "/usr/local/bin:/usr/bin::/home/u/bin", Files.Contains));

    [Fact]
    public void MissingPathMeansNothingIsFound() =>
        Assert.False(ProcessRunner.ExistsOnPath("git", null, Files.Contains));
}

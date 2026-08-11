using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

/// <summary>
/// S14b: SyncFolderScanner.DetectFrom is the pure, testable core behind the
/// Backup tab's "Detect..." button - see the design brief's "why this
/// feature exists" note (finding the sync folder automatically is the
/// biggest usability win of the sync-folder transport). Every real-machine
/// input (environment variables, %USERPROFILE%, the drive roots to probe,
/// the mapped-network-drive listing) is passed in explicitly, and even the
/// existence check is an injected delegate, so this can be tested fully
/// without touching the real registry, real environment variables, or a real
/// NAS share - mirroring ClaudeLocationScannerTests' own approach for the
/// existing scanner.
/// </summary>
public class SyncFolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sfs-{Guid.NewGuid():N}");

    public SyncFolderScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static readonly Func<string, bool> AlwaysExists = _ => true;
    private static readonly Func<string, bool> NeverExists = _ => false;

    [Fact]
    public void FindsOneDriveFromCommercialEnvVar()
    {
        var path = Path.Combine(_root, "OneDrive - Go2Cloud (PTY) LTD");
        var envVars = new Dictionary<string, string?> { ["OneDriveCommercial"] = path };

        var found = SyncFolderScanner.DetectFrom(envVars, "", Array.Empty<string>(), Array.Empty<(char, string)>(), AlwaysExists);

        Assert.Contains(found, c => c.Path == path);
    }

    [Fact]
    public void DeduplicatesWhenMultipleOneDriveVarsPointToTheSamePath()
    {
        var path = Path.Combine(_root, "OneDrive - Go2Cloud (PTY) LTD");
        var envVars = new Dictionary<string, string?>
        {
            ["OneDrive"] = path,
            ["OneDriveCommercial"] = path,
        };

        var found = SyncFolderScanner.DetectFrom(envVars, "", Array.Empty<string>(), Array.Empty<(char, string)>(), AlwaysExists);

        Assert.Single(found, c => c.Path == path);
    }

    [Fact]
    public void DeduplicatesCaseInsensitivelyAndIgnoringATrailingSeparator()
    {
        var canonical = Path.Combine(_root, "OneDrive");
        var envVars = new Dictionary<string, string?>
        {
            ["OneDrive"] = canonical.ToUpperInvariant(),
            ["OneDriveCommercial"] = canonical + Path.DirectorySeparatorChar,
        };

        var found = SyncFolderScanner.DetectFrom(envVars, "", Array.Empty<string>(), Array.Empty<(char, string)>(), AlwaysExists);

        Assert.Single(found);
    }

    [Fact]
    public void KeepsDistinctOneDrivePathsWhenPersonalAndWorkDiffer()
    {
        var personal = Path.Combine(_root, "OneDrive-Personal");
        var work = Path.Combine(_root, "OneDrive-Work");
        var envVars = new Dictionary<string, string?>
        {
            ["OneDriveConsumer"] = personal,
            ["OneDriveCommercial"] = work,
        };

        var found = SyncFolderScanner.DetectFrom(envVars, "", Array.Empty<string>(), Array.Empty<(char, string)>(), AlwaysExists);

        Assert.Contains(found, c => c.Path == personal);
        Assert.Contains(found, c => c.Path == work);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void FindsGoogleDriveUnderUserProfile()
    {
        var found = SyncFolderScanner.DetectFrom(
            new Dictionary<string, string?>(), _root, Array.Empty<string>(), Array.Empty<(char, string)>(), AlwaysExists);

        Assert.Contains(found, c => c.Path == Path.Combine(_root, "Google Drive"));
    }

    [Fact]
    public void FindsGoogleDriveViaMyDriveFolderOnAnyProbedDriveRoot()
    {
        var driveRoots = new[] { @"G:\" };
        var found = SyncFolderScanner.DetectFrom(
            new Dictionary<string, string?>(), "", driveRoots, Array.Empty<(char, string)>(),
            path => path == @"G:\My Drive");

        Assert.Contains(found, c => c.Path == @"G:\My Drive");
    }

    [Fact]
    public void DoesNotOfferAMyDriveFolderThatDoesNotExistOnAProbedRoot()
    {
        var driveRoots = new[] { @"H:\" };
        var found = SyncFolderScanner.DetectFrom(
            new Dictionary<string, string?>(), "", driveRoots, Array.Empty<(char, string)>(), NeverExists);

        Assert.DoesNotContain(found, c => c.Path == @"H:\My Drive");
    }

    [Fact]
    public void FindsDropboxUnderUserProfile()
    {
        var found = SyncFolderScanner.DetectFrom(
            new Dictionary<string, string?>(), _root, Array.Empty<string>(), Array.Empty<(char, string)>(), AlwaysExists);

        Assert.Contains(found, c => c.Path == Path.Combine(_root, "Dropbox"));
    }

    [Fact]
    public void OffersTheUncPathNotTheDriveLetterForAMappedNetworkDrive()
    {
        var mappedDrives = new[] { ('M', @"\\192.168.1.210\media") };
        var found = SyncFolderScanner.DetectFrom(
            new Dictionary<string, string?>(), "", Array.Empty<string>(), mappedDrives, AlwaysExists);

        var candidate = Assert.Single(found, c => c.Path == @"\\192.168.1.210\media");
        // Never offers "M:\..." for a mapped drive - see the class's own doc
        // comment on why (a drive-letter mapping is per-session and is not
        // guaranteed to resolve under Task Scheduler).
        Assert.DoesNotContain(found, c => c.Path.StartsWith("M:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("M", candidate.DisplayName);
    }

    [Fact]
    public void SkipsCandidatesThatDoNotActuallyExist()
    {
        var envVars = new Dictionary<string, string?> { ["OneDrive"] = Path.Combine(_root, "nope") };

        var found = SyncFolderScanner.DetectFrom(envVars, _root, Array.Empty<string>(), Array.Empty<(char, string)>(), NeverExists);

        Assert.Empty(found);
    }

    [Fact]
    public void EmptyInputsProduceAnEmptyResultWithoutThrowing()
    {
        var found = SyncFolderScanner.DetectFrom(
            new Dictionary<string, string?>(), "", Array.Empty<string>(), Array.Empty<(char, string)>(), NeverExists);

        Assert.Empty(found);
    }

    [Fact]
    public void ToleratesAnExistenceCheckThatThrowsForOneCandidateWithoutAbortingTheRest()
    {
        var good = Path.Combine(_root, "Dropbox");
        var envVars = new Dictionary<string, string?> { ["OneDrive"] = "\0bad\0path" };

        var found = SyncFolderScanner.DetectFrom(
            envVars, _root, Array.Empty<string>(), Array.Empty<(char, string)>(),
            path => path == good);

        Assert.Single(found, c => c.Path == good);
    }

    [Fact]
    public void BlankEnvVarValueIsIgnored()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["OneDrive"] = "",
            ["OneDriveConsumer"] = "   ",
            ["OneDriveCommercial"] = null,
        };

        var found = SyncFolderScanner.DetectFrom(envVars, "", Array.Empty<string>(), Array.Empty<(char, string)>(), AlwaysExists);

        Assert.Empty(found);
    }

    [Fact]
    public void RealDetectNeverThrowsAndReturnsAList()
    {
        // Not asserting on contents (machine-dependent, like
        // ClaudeLocationScannerTests.ProbeBuildsCandidatesFromRealKnownFolders) -
        // just that the real entry point (real env vars, real drive letters,
        // real registry) never throws, degrading to "found nothing" instead.
        var found = SyncFolderScanner.Detect();
        Assert.NotNull(found);
    }

    [Fact]
    public void RealReadMappedNetworkDrivesNeverThrows()
    {
        var found = SyncFolderScanner.ReadMappedNetworkDrives();
        Assert.NotNull(found);
    }
}

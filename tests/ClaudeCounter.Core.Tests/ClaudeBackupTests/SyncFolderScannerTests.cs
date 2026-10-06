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

    // Drive letters: Path.Combine(@"G:\", ...) only means "G:\My Drive" on Windows.
    [WindowsOnlyFact]
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

    [WindowsOnlyFact]
    public void RealDetectNeverThrowsAndReturnsAList()
    {
        if (!OperatingSystem.IsWindows())
            return; // the guard CA1416 needs; the attribute already skips elsewhere

        // Not asserting on contents (machine-dependent, like
        // ClaudeLocationScannerTests.ProbeBuildsCandidatesFromRealKnownFolders) -
        // just that the real entry point (real env vars, real drive letters,
        // real registry) never throws, degrading to "found nothing" instead.
        var found = SyncFolderScanner.Detect();
        Assert.NotNull(found);
    }

    [WindowsOnlyFact]
    public void RealReadMappedNetworkDrivesNeverThrows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var found = SyncFolderScanner.ReadMappedNetworkDrives();
        Assert.NotNull(found);
    }
}

/// <summary>
/// S16: SyncFolderScanner.FilterByProvider is what lets the Backup tab's
/// named-destination selector ("OneDrive", "NAS / network share", ...) offer
/// only the candidates that plausibly match whichever destination the user
/// picked, instead of every sync folder found on the machine. Built on the
/// same DisplayName-prefix convention DetectFrom itself already produces -
/// see that method's own candidates.
/// </summary>
public class SyncFolderScannerFilterByProviderTests
{
    [Fact]
    public void OneDriveFilterKeepsOnlyOneDriveCandidates()
    {
        var candidates = new[]
        {
            new SyncFolderCandidate("OneDrive - Work/School", @"C:\OneDriveWork"),
            new SyncFolderCandidate("OneDrive - Personal", @"C:\OneDrivePersonal"),
            new SyncFolderCandidate("Google Drive", @"C:\GDrive"),
            new SyncFolderCandidate("Dropbox", @"C:\Dropbox"),
        };

        var filtered = SyncFolderScanner.FilterByProvider(candidates, SyncProvider.OneDrive);

        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, c => Assert.StartsWith("OneDrive", c.DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public void GoogleDriveFilterKeepsOnlyGoogleDriveCandidates()
    {
        var candidates = new[]
        {
            new SyncFolderCandidate("Google Drive", @"C:\Users\me\Google Drive"),
            new SyncFolderCandidate("Google Drive (G:)", @"G:\My Drive"),
            new SyncFolderCandidate("OneDrive", @"C:\OneDrive"),
        };

        var filtered = SyncFolderScanner.FilterByProvider(candidates, SyncProvider.GoogleDrive);

        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, c => Assert.StartsWith("Google Drive", c.DisplayName, StringComparison.Ordinal));
    }

    [Fact]
    public void DropboxFilterKeepsOnlyDropboxCandidates()
    {
        var candidates = new[]
        {
            new SyncFolderCandidate("Dropbox", @"C:\Dropbox"),
            new SyncFolderCandidate("OneDrive", @"C:\OneDrive"),
        };

        var filtered = SyncFolderScanner.FilterByProvider(candidates, SyncProvider.Dropbox);

        Assert.Single(filtered, c => c.Path == @"C:\Dropbox");
    }

    // The single most important behaviour of this filter: for NAS, it must
    // only ever pass through the UNC-form candidates DetectFrom already
    // produces for a mapped drive - never a drive-letter path - mirroring
    // OffersTheUncPathNotTheDriveLetterForAMappedNetworkDrive above, just
    // exercised through FilterByProvider instead of DetectFrom directly.
    [Fact]
    public void NasFilterKeepsOnlyUncCandidatesNeverADriveLetter()
    {
        var mappedDrives = new[] { ('M', @"\\192.168.1.210\media") };
        var all = SyncFolderScanner.DetectFrom(
            new Dictionary<string, string?> { ["OneDrive"] = @"C:\OneDrive" },
            "", Array.Empty<string>(), mappedDrives, _ => true);

        var filtered = SyncFolderScanner.FilterByProvider(all, SyncProvider.Nas);

        var candidate = Assert.Single(filtered, c => c.Path == @"\\192.168.1.210\media");
        Assert.StartsWith(@"\\", candidate.Path);
        Assert.DoesNotContain(filtered, c => c.Path.StartsWith("M:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OtherProviderMatchesNothing()
    {
        var candidates = new[]
        {
            new SyncFolderCandidate("Dropbox", @"C:\Dropbox"),
            new SyncFolderCandidate("NAS share (M: -> \\\\nas\\share)", @"\\nas\share"),
        };

        Assert.Empty(SyncFolderScanner.FilterByProvider(candidates, SyncProvider.Other));
    }

    [Fact]
    public void EmptyCandidateListProducesEmptyResult()
    {
        Assert.Empty(SyncFolderScanner.FilterByProvider(Array.Empty<SyncFolderCandidate>(), SyncProvider.OneDrive));
    }
}

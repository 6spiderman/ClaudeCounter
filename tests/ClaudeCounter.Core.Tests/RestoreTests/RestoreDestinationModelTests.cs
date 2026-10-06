// tests/ClaudeCounter.Tests/RestoreTests/RestoreDestinationModelTests.cs
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

/// <summary>
/// S10 (restore to a different folder): RestoreDialog's destination choice
/// is a thin WinForms adapter over RestoreDestinationModel (mirrors
/// RestoreDisplayModel / RestoreDialog's preview grid) - these tests
/// exercise the resolution and refusal rules directly, without ever
/// constructing a Form.
/// </summary>
public class RestoreDestinationModelTests
{
    private const string LiveRoot = @"C:\Users\test\.claude";

    // --- ResolveDestinationRoot ---------------------------------------

    [Fact]
    public void LiveAlwaysResolvesToLiveRootEvenWithAStaleCustomRootSet()
    {
        var resolved = RestoreDestinationModel.ResolveDestinationRoot(
            RestoreDestinationKind.Live, LiveRoot, customRoot: @"D:\leftover-from-before");

        Assert.Equal(LiveRoot, resolved);
    }

    [Fact]
    public void CustomResolvesToTheCustomRootWhenSet()
    {
        var resolved = RestoreDestinationModel.ResolveDestinationRoot(
            RestoreDestinationKind.Custom, LiveRoot, customRoot: @"D:\scratch\restore-here");

        Assert.Equal(@"D:\scratch\restore-here", resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CustomWithNoFolderChosenYetResolvesToNullNotLiveRoot(string? customRoot)
    {
        // Falling back to Live here would let a Preview click restore into
        // live config while the UI still reads "Another folder..." - the
        // exact confusion this feature exists to prevent.
        var resolved = RestoreDestinationModel.ResolveDestinationRoot(
            RestoreDestinationKind.Custom, LiveRoot, customRoot);

        Assert.Null(resolved);
    }

    // --- IsRefusedDestination -------------------------------------------

    // Built under the temp folder rather than as C:\ literals, so the overlap
    // checks run with this platform's real separators (on Linux CI too).
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cc-restore-dest");

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string Elsewhere(params string[] parts) =>
        Path.Combine([Path.GetTempPath(), "cc-restore-elsewhere", .. parts]);

    [Fact]
    public void CandidateEqualToAProtectedDirIsRefused()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            P("staging"), new[] { P("staging") });

        Assert.True(refused);
    }

    [Fact]
    public void CandidateNestedInsideAProtectedDirIsRefused()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            P("staging", "subfolder"), new[] { P("staging") });

        Assert.True(refused);
    }

    [Fact]
    public void AProtectedDirNestedInsideTheCandidateIsRefused()
    {
        // The other direction: choosing an ANCESTOR of a protected scratch
        // directory as the destination is just as unsafe - Overlaps covers
        // both directions for exactly this reason.
        var refused = RestoreDestinationModel.IsRefusedDestination(
            P("staging"), new[] { P("staging", "subfolder") });

        Assert.True(refused);
    }

    [Fact]
    public void SiblingWithASharedNamePrefixIsNotRefused()
    {
        // "staging-evil" is not inside "staging" - a naive
        // StartsWith-based check would wrongly refuse it. RelativePathGuard
        // is separator-aware and must not make that mistake here either.
        var refused = RestoreDestinationModel.IsRefusedDestination(
            P("staging-evil"), new[] { P("staging") });

        Assert.False(refused);
    }

    [Fact]
    public void UnrelatedCandidateIsNotRefused()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            Elsewhere("backups", "restored"), new[] { P("staging"), P("safety"), P("drive-temp") });

        Assert.False(refused);
    }

    [Fact]
    public void CandidateIsCheckedAgainstEveryProtectedDirNotJustTheFirst()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            P("safety", "2026-08-11"), new[] { P("staging"), P("safety"), P("drive-temp") });

        Assert.True(refused);
    }

    [Fact]
    public void NoProtectedDirsMeansNothingIsEverRefused()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            @"D:\anywhere", Array.Empty<string>());

        Assert.False(refused);
    }
}

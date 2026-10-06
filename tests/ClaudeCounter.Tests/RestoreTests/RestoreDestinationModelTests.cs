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

    [Fact]
    public void CandidateEqualToAProtectedDirIsRefused()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            @"C:\staging", new[] { @"C:\staging" });

        Assert.True(refused);
    }

    [Fact]
    public void CandidateNestedInsideAProtectedDirIsRefused()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            @"C:\staging\subfolder", new[] { @"C:\staging" });

        Assert.True(refused);
    }

    [Fact]
    public void AProtectedDirNestedInsideTheCandidateIsRefused()
    {
        // The other direction: choosing an ANCESTOR of a protected scratch
        // directory as the destination is just as unsafe - Overlaps covers
        // both directions for exactly this reason.
        var refused = RestoreDestinationModel.IsRefusedDestination(
            @"C:\staging", new[] { @"C:\staging\subfolder" });

        Assert.True(refused);
    }

    [Fact]
    public void SiblingWithASharedNamePrefixIsNotRefused()
    {
        // "C:\staging-evil" is not inside "C:\staging" - a naive
        // StartsWith-based check would wrongly refuse it. RelativePathGuard
        // is separator-aware and must not make that mistake here either.
        var refused = RestoreDestinationModel.IsRefusedDestination(
            @"C:\staging-evil", new[] { @"C:\staging" });

        Assert.False(refused);
    }

    [Fact]
    public void UnrelatedCandidateIsNotRefused()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            @"D:\backups\restored", new[] { @"C:\staging", @"C:\safety", @"C:\drive-temp" });

        Assert.False(refused);
    }

    [Fact]
    public void CandidateIsCheckedAgainstEveryProtectedDirNotJustTheFirst()
    {
        var refused = RestoreDestinationModel.IsRefusedDestination(
            @"C:\safety\2026-08-11", new[] { @"C:\staging", @"C:\safety", @"C:\drive-temp" });

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

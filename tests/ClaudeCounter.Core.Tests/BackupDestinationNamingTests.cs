using ClaudeBackup;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// BackupDestinationNaming is the pure, testable core behind the
/// destinations dialog's "Add..." kind picker and default naming - public
/// and static (no Form), for the same reason as
/// SettingsForm.HasEmbeddedCredential/InitialDestinationIndex(-now-removed):
/// this project's tests never construct a Form except via the dedicated STA
/// smoke-test helper. Covers the task brief's explicit "distinguishable
/// default names" requirement (two NAS shares, two GitHub repos) and the
/// Kind/SyncProvider display mapping shared by the Backup tab's summary and
/// the destinations dialog's own list.
/// </summary>
public class BackupDestinationNamingTests
{
    [Fact]
    public void KindOptionsHasExactlySixEntriesCoveringEveryKind()
    {
        Assert.Equal(6, BackupDestinationNaming.KindOptions.Length);
        Assert.Contains(BackupDestinationNaming.KindOptions, o => o.Kind == DestinationKind.GitHub);
        Assert.Equal(4, BackupDestinationNaming.KindOptions.Count(o => o.Kind == DestinationKind.SyncFolder));
        Assert.Single(BackupDestinationNaming.KindOptions, o => o.Kind == DestinationKind.Rclone);
    }

    [Fact]
    public void KindOptionsSyncFolderEntriesCoverEveryNamedProvider()
    {
        var providers = BackupDestinationNaming.KindOptions
            .Where(o => o.Kind == DestinationKind.SyncFolder)
            .Select(o => o.Provider)
            .ToList();
        Assert.Contains(SyncProvider.GoogleDrive, providers);
        Assert.Contains(SyncProvider.OneDrive, providers);
        Assert.Contains(SyncProvider.Dropbox, providers);
        Assert.Contains(SyncProvider.Nas, providers);
    }

    [Theory]
    [InlineData(DestinationKind.GitHub, SyncProvider.Other, "GitHub")]
    [InlineData(DestinationKind.SyncFolder, SyncProvider.GoogleDrive, "Google Drive")]
    [InlineData(DestinationKind.SyncFolder, SyncProvider.OneDrive, "OneDrive")]
    [InlineData(DestinationKind.SyncFolder, SyncProvider.Dropbox, "Dropbox")]
    [InlineData(DestinationKind.SyncFolder, SyncProvider.Nas, "NAS / network share")]
    [InlineData(DestinationKind.SyncFolder, SyncProvider.Other, "Sync folder")]
    [InlineData(DestinationKind.Rclone, SyncProvider.Other, "rclone remote")]
    public void KindDisplayNameMapsEveryKindProviderPair(DestinationKind kind, SyncProvider provider, string expected)
    {
        Assert.Equal(expected, BackupDestinationNaming.KindDisplayName(kind, provider));
    }

    // The picker's own Label carries a parenthetical qualifier ("(sync
    // folder)", "(advanced)") for context at pick time - KindDisplayName
    // must NOT echo that into a destination's default name or the "Kind"
    // column, since "OneDrive (sync folder)" would be a strange thing for a
    // destination to be named.
    [Fact]
    public void KindDisplayNameNeverIncludesThePickersParentheticalQualifier()
    {
        foreach (var option in BackupDestinationNaming.KindOptions)
        {
            var displayName = BackupDestinationNaming.KindDisplayName(option.Kind, option.Provider);
            Assert.DoesNotContain("(sync folder)", displayName);
            Assert.DoesNotContain("(advanced)", displayName);
        }
    }

    [Fact]
    public void GenerateUniqueNameReturnsTheBaseNameWhenNothingCollides()
    {
        Assert.Equal("GitHub", BackupDestinationNaming.GenerateUniqueName("GitHub", Array.Empty<string>()));
        Assert.Equal("GitHub", BackupDestinationNaming.GenerateUniqueName("GitHub", new[] { "Home NAS", "Office NAS" }));
    }

    // The task brief's explicit scenario: two GitHub repos, or two NAS
    // shares, must both be addable with distinguishable default names.
    [Fact]
    public void GenerateUniqueNameAppendsAnIncrementingSuffixOnCollision()
    {
        Assert.Equal("GitHub (2)", BackupDestinationNaming.GenerateUniqueName("GitHub", new[] { "GitHub" }));
        Assert.Equal("GitHub (3)", BackupDestinationNaming.GenerateUniqueName("GitHub", new[] { "GitHub", "GitHub (2)" }));
        Assert.Equal("NAS / network share (2)",
            BackupDestinationNaming.GenerateUniqueName("NAS / network share", new[] { "NAS / network share" }));
    }

    // A gap in the sequence (e.g. "GitHub (2)" was renamed away, leaving
    // "GitHub" and "GitHub (3)") must not stop at the gap - the first
    // genuinely free suffix wins, not the first missing one skipped over.
    [Fact]
    public void GenerateUniqueNameFillsTheFirstFreeSuffixEvenWithAGapInExistingNames()
    {
        Assert.Equal("GitHub (2)", BackupDestinationNaming.GenerateUniqueName("GitHub", new[] { "GitHub", "GitHub (3)" }));
    }

    [Fact]
    public void GenerateUniqueNameComparisonIsCaseInsensitive()
    {
        Assert.Equal("GitHub (2)", BackupDestinationNaming.GenerateUniqueName("GitHub", new[] { "github" }));
    }
}

// tests/ClaudeCounter.Tests/RestoreTests/RestoreDisplayModelTests.cs
using ClaudeBackup;
using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests.Restore;

/// <summary>
/// RestoreDialog's preview grid is a thin WinForms adapter over
/// RestoreDisplayModel (mirrors BackupPickerDialog / BackupTreeModel) -
/// these tests exercise the actual selectability/default-selection rule
/// (design spec step 5: "default to all New and Changed, nothing else")
/// without ever constructing a Form.
/// </summary>
public class RestoreDisplayModelTests
{
    private static RestoreFileEntry Entry(string rel, RestoreFileStatus status) =>
        new(rel, status, StagedSizeBytes: 1, StagedModifiedUtc: DateTimeOffset.UnixEpoch,
            LiveSizeBytes: 1, LiveModifiedUtc: DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(RestoreFileStatus.New, true)]
    [InlineData(RestoreFileStatus.Changed, true)]
    [InlineData(RestoreFileStatus.Identical, false)]
    [InlineData(RestoreFileStatus.LiveOnly, false)]
    public void IsSelectableIsTrueOnlyForNewAndChanged(RestoreFileStatus status, bool expected)
    {
        Assert.Equal(expected, RestoreDisplayModel.IsSelectable(status));
    }

    [Theory]
    [InlineData(RestoreFileStatus.New, true)]
    [InlineData(RestoreFileStatus.Changed, true)]
    [InlineData(RestoreFileStatus.Identical, false)]
    [InlineData(RestoreFileStatus.LiveOnly, false)]
    public void BuildRowsDefaultsCheckedToExactlyWhatIsSelectable(RestoreFileStatus status, bool expectedChecked)
    {
        var rows = RestoreDisplayModel.BuildRows(new[] { Entry("a.txt", status) });

        var row = Assert.Single(rows);
        Assert.Equal(expectedChecked, row.Selectable);
        Assert.Equal(expectedChecked, row.DefaultChecked);
    }

    // Restore rule 3, at the UI layer: however BuildRows is used, a LiveOnly
    // row can never come out DefaultChecked - this is the property the
    // dialog's checkbox handling relies on to guarantee restore never builds
    // an apply set containing one.
    [Fact]
    public void ALiveOnlyRowIsNeverSelectableOrDefaultChecked()
    {
        var rows = RestoreDisplayModel.BuildRows(new[]
        {
            Entry("kept-live.txt", RestoreFileStatus.LiveOnly),
        });

        var row = Assert.Single(rows);
        Assert.False(row.Selectable);
        Assert.False(row.DefaultChecked);
    }

    [Fact]
    public void BuildRowsPreservesClassifyOrdering()
    {
        var entries = new[]
        {
            Entry("z.txt", RestoreFileStatus.New),
            Entry("a.txt", RestoreFileStatus.Changed),
            Entry("m.txt", RestoreFileStatus.Identical),
        };

        var rows = RestoreDisplayModel.BuildRows(entries);

        Assert.Equal(new[] { "z.txt", "a.txt", "m.txt" }, rows.Select(r => r.Entry.RelativePath));
    }

    [Fact]
    public void SummarizeCountsEachStatusIndependently()
    {
        var rows = RestoreDisplayModel.BuildRows(new[]
        {
            Entry("n1.txt", RestoreFileStatus.New),
            Entry("n2.txt", RestoreFileStatus.New),
            Entry("c1.txt", RestoreFileStatus.Changed),
            Entry("i1.txt", RestoreFileStatus.Identical),
            Entry("l1.txt", RestoreFileStatus.LiveOnly),
            Entry("l2.txt", RestoreFileStatus.LiveOnly),
            Entry("l3.txt", RestoreFileStatus.LiveOnly),
        });

        var counts = RestoreDisplayModel.Summarize(rows);

        Assert.Equal(2, counts.New);
        Assert.Equal(1, counts.Changed);
        Assert.Equal(1, counts.Identical);
        Assert.Equal(3, counts.LiveOnly);
        Assert.Equal(7, counts.Total);
    }

    [Fact]
    public void SummarizeOfNoRowsIsAllZero()
    {
        var counts = RestoreDisplayModel.Summarize(Array.Empty<RestoreFileRow>());

        Assert.Equal(0, counts.New);
        Assert.Equal(0, counts.Changed);
        Assert.Equal(0, counts.Identical);
        Assert.Equal(0, counts.LiveOnly);
        Assert.Equal(0, counts.Total);
    }
}

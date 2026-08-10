using ClaudeCounter.UI;
using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// Pure logic tests for the picker's tree model - no Form, no TreeView. See
/// SettingsFormSmokeTests for the WinForms adapter's own construction smoke
/// test.
/// </summary>
public class BackupTreeModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"btm-{Guid.NewGuid():N}");

    public BackupTreeModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private string P(params string[] segments) => Path.Combine(new[] { _root }.Concat(segments).ToArray());

    private void WriteFile(string relative, string content = "x")
    {
        var full = P(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ---- Mapping: directory -> dir/**, file -> literal path ----

    [Fact]
    public void TickingADirectoryGeneratesDoubleStarPattern()
    {
        Directory.CreateDirectory(P("commands"));
        WriteFile("commands/a.md");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var commands = model.Root.Children.Single(c => c.Name == "commands");

        Assert.True(model.SetChecked(commands, true));
        Assert.Equal(new[] { "commands/**" }, model.GeneratePatterns());
    }

    [Fact]
    public void TickingAFileGeneratesItsLiteralRelativePath()
    {
        WriteFile("settings.json", "{}");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var settings = model.Root.Children.Single(c => c.Name == "settings.json");

        Assert.True(model.SetChecked(settings, true));
        Assert.Equal(new[] { "settings.json" }, model.GeneratePatterns());
    }

    [Fact]
    public void UntickingAChildLeavesTheParentIndeterminateNotFullySelected()
    {
        WriteFile("commands/a.md");
        WriteFile("commands/b.md");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var commands = model.Root.Children.Single(c => c.Name == "commands");
        model.EnsureChildrenLoaded(commands);
        var a = commands.Children.Single(c => c.Name == "a.md");
        var b = commands.Children.Single(c => c.Name == "b.md");

        model.SetChecked(a, true);
        model.SetChecked(b, true);
        Assert.Equal(NodeCheckState.Checked, commands.CheckState);

        model.SetChecked(b, false);
        Assert.Equal(NodeCheckState.Indeterminate, commands.CheckState);
        Assert.Equal(new[] { "commands/a.md" }, model.GeneratePatterns());
    }

    // ---- Round trip: config -> tree -> config ----

    [Fact]
    public void RoundTripsARepresentableDirectoryPattern()
    {
        WriteFile("agents/foo.md");
        var model = BackupTreeModel.LoadFromPatterns(_root, new[] { "agents/**" });
        model.EnsureChildrenLoaded(model.Root);

        var agents = model.Root.Children.Single(c => c.Name == "agents");
        Assert.Equal(NodeCheckState.Checked, agents.CheckState);
        Assert.Equal(new[] { "agents/**" }, model.GeneratePatterns());
        Assert.Empty(model.UnrepresentablePatterns);
    }

    [Fact]
    public void RoundTripsARepresentableLiteralFilePattern()
    {
        WriteFile("CLAUDE.md");
        var model = BackupTreeModel.LoadFromPatterns(_root, new[] { "CLAUDE.md" });
        model.EnsureChildrenLoaded(model.Root);

        var claudeMd = model.Root.Children.Single(c => c.Name == "CLAUDE.md");
        Assert.Equal(NodeCheckState.Checked, claudeMd.CheckState);
        Assert.Equal(new[] { "CLAUDE.md" }, model.GeneratePatterns());
    }

    [Fact]
    public void PreservesAHandWrittenNonRepresentablePatternUntouched()
    {
        WriteFile("settings.json");
        var model = BackupTreeModel.LoadFromPatterns(_root, new[] { "settings.json", "**/*.md", "plugins/*.json" });

        Assert.Equal(new[] { "**/*.md", "plugins/*.json" }, model.UnrepresentablePatterns);

        var patterns = model.GeneratePatterns();
        Assert.Contains("**/*.md", patterns);
        Assert.Contains("plugins/*.json", patterns);
    }

    [Fact]
    public void OpeningAndClosingTheDialogWithoutTouchingAnythingLosesNoPattern()
    {
        WriteFile("settings.json");
        WriteFile("commands/a.md");
        var original = new[] { "settings.json", "commands/**", "**/*.md", "nonexistent-file.txt" };

        var model = BackupTreeModel.LoadFromPatterns(_root, original);
        // Simulate "opened the dialog" (root auto-loads) then closed without
        // any tick - GeneratePatterns must reproduce exactly the original
        // set (order aside), including the pattern for a file that does not
        // exist on disk (nonexistent-file.txt: correctly not representable,
        // but still must not be dropped).
        model.EnsureChildrenLoaded(model.Root);

        var result = model.GeneratePatterns();
        Assert.Equal(original.OrderBy(x => x, StringComparer.Ordinal),
            result.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void APatternForAMissingTargetIsPreservedNotDropped()
    {
        // "settings.json" pattern but the file does not actually exist under
        // this root - must not silently vanish just because the tree cannot
        // find anything to tick.
        var model = BackupTreeModel.LoadFromPatterns(_root, new[] { "settings.json" });
        Assert.Equal(new[] { "settings.json" }, model.UnrepresentablePatterns);
        Assert.Equal(new[] { "settings.json" }, model.GeneratePatterns());
    }

    [Fact]
    public void DeeperUnvisitedRepresentablePatternSurvivesWithoutBeingExpanded()
    {
        WriteFile("plugins/sub/foo.json");
        var model = BackupTreeModel.LoadFromPatterns(_root, new[] { "plugins/sub/**" });

        // Only load the root level - never expand into "plugins".
        model.EnsureChildrenLoaded(model.Root);

        Assert.Equal(new[] { "plugins/sub/**" }, model.GeneratePatterns());
    }

    // ---- Display cap is display-only ----

    [Fact]
    public void ParentTickSelectsTheWholeDirectoryEvenWhenChildrenWereDisplayCapped()
    {
        for (var i = 0; i < 5; i++)
            WriteFile($"skills/file{i}.txt");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var skills = model.Root.Children.Single(c => c.Name == "skills");

        // Cap far below the real count (5) to exercise the same code path
        // the picker's real ~500 cap would, without needing 500+ files.
        model.EnsureChildrenLoaded(skills, cap: 2);
        Assert.Equal(2, skills.Children.Count);
        Assert.Equal(3, skills.HiddenChildCount);

        Assert.True(model.SetChecked(skills, true));

        // The whole directory is selected via a single dir/** pattern -
        // the 3 files never rendered are still covered by it, not silently
        // dropped from the backup.
        Assert.Equal(new[] { "skills/**" }, model.GeneratePatterns());
    }

    [Fact]
    public void TickingAllRenderedChildrenDoesNotImplyTheHiddenOnesWereSelected()
    {
        for (var i = 0; i < 5; i++)
            WriteFile($"skills/file{i}.txt");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var skills = model.Root.Children.Single(c => c.Name == "skills");
        model.EnsureChildrenLoaded(skills, cap: 2);

        foreach (var child in skills.Children)
            model.SetChecked(child, true);

        // All 2 RENDERED children are checked, but 3 real files were never
        // shown - the directory must stay Indeterminate, not silently
        // promote to Checked (which would imply the hidden 3 were selected
        // too, when nobody ever saw or ticked them).
        Assert.Equal(NodeCheckState.Indeterminate, skills.CheckState);

        var patterns = model.GeneratePatterns();
        Assert.DoesNotContain("skills/**", patterns);
        Assert.Equal(2, patterns.Count);
    }

    // ---- Denylist ----

    [Fact]
    public void DenylistedNodeIsMarkedUnselectableAndCannotBeTicked()
    {
        WriteFile(".credentials.json", "secret");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var creds = model.Root.Children.Single(c => c.Name == ".credentials.json");

        Assert.True(creds.IsDenylisted);
        Assert.NotNull(creds.DenylistReason);

        Assert.False(model.SetChecked(creds, true));
        Assert.Equal(NodeCheckState.Unchecked, creds.CheckState);
        Assert.Empty(model.GeneratePatterns());
    }

    [Fact]
    public void ADenylistedFileTickingSiblingsDoesNotPromoteParentToFullySelected()
    {
        WriteFile("commands/a.md");
        WriteFile("commands/.credentials.json", "secret");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var commands = model.Root.Children.Single(c => c.Name == "commands");
        model.EnsureChildrenLoaded(commands);
        var a = commands.Children.Single(c => c.Name == "a.md");

        model.SetChecked(a, true);

        // The denylisted sibling is excluded from the vote entirely, so
        // ticking the only selectable child fully satisfies the parent.
        Assert.Equal(NodeCheckState.Checked, commands.CheckState);
        Assert.Equal(new[] { "commands/**" }, model.GeneratePatterns());
    }

    [Fact]
    public void APatternPointingAtADenylistedFileIsPreservedAsUnrepresentableNotSilentlyTicked()
    {
        WriteFile(".credentials.json", "secret");
        var model = BackupTreeModel.LoadFromPatterns(_root, new[] { ".credentials.json" });

        Assert.Equal(new[] { ".credentials.json" }, model.UnrepresentablePatterns);

        model.EnsureChildrenLoaded(model.Root);
        var creds = model.Root.Children.Single(c => c.Name == ".credentials.json");
        Assert.Equal(NodeCheckState.Unchecked, creds.CheckState);

        // Still round-trips through the box, just never via a tick.
        Assert.Equal(new[] { ".credentials.json" }, model.GeneratePatterns());
    }

    // ---- Transcript-bearing note ----

    [Fact]
    public void TranscriptBearingTopLevelEntriesCarryANoteAndStayUntickedByDefault()
    {
        Directory.CreateDirectory(P("projects"));
        WriteFile("projects/x/big.log");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var projects = model.Root.Children.Single(c => c.Name == "projects");

        Assert.True(projects.IsTranscriptBearing);
        Assert.NotNull(projects.TranscriptNote);
        Assert.Equal(NodeCheckState.Unchecked, projects.CheckState);
        Assert.False(projects.IsDenylisted); // selectable, just annotated
    }

    // ---- Sorting: directories before files ----

    [Fact]
    public void ChildrenSortDirectoriesBeforeFiles()
    {
        WriteFile("zzz-file.txt");
        Directory.CreateDirectory(P("aaa-dir"));

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);

        Assert.True(model.Root.Children[0].IsDirectory);
        Assert.Equal("aaa-dir", model.Root.Children[0].Name);
        Assert.False(model.Root.Children[1].IsDirectory);
        Assert.Equal("zzz-file.txt", model.Root.Children[1].Name);
    }

    // ---- Lazy loading ----

    [Fact]
    public void ChildrenAreNotLoadedUntilEnsureChildrenLoadedIsCalled()
    {
        WriteFile("commands/a.md");

        var model = new BackupTreeModel(_root);
        Assert.False(model.Root.ChildrenLoaded);
        Assert.Empty(model.Root.Children);

        model.EnsureChildrenLoaded(model.Root);
        Assert.True(model.Root.ChildrenLoaded);
        var commands = model.Root.Children.Single(c => c.Name == "commands");
        Assert.False(commands.ChildrenLoaded);
        Assert.Empty(commands.Children); // not walked into just because the parent was loaded
    }

    [Fact]
    public void EnsureChildrenLoadedOnAMissingDirectoryDoesNotThrow()
    {
        var model = new BackupTreeModel(Path.Combine(_root, "does-not-exist"));
        model.EnsureChildrenLoaded(model.Root);
        Assert.Empty(model.Root.Children);
    }
}

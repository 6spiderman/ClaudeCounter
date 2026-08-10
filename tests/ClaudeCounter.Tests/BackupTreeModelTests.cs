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

    // ---- Deep partial selection (formerly "display cap is display-only") ----
    //
    // Fix round 2: EnsureChildrenLoaded's ~500-entry display cap - and the
    // three tests that used to live in this section covering it
    // (ParentTickSelectsTheWholeDirectoryEvenWhenChildrenWereDisplayCapped,
    // TickingAllRenderedChildrenDoesNotImplyTheHiddenOnesWereSelected, and
    // fix round 1's own
    // UntickingOneRenderedChildOfACappedCheckedParentStillCoversTheHiddenSiblings)
    // - are GONE. Two rounds of this review both found and fixed a CRITICAL
    // data-loss bug caused by that cap (round 1: ticking a capped directory
    // then unticking one rendered child dropped whatever the cap had left
    // unrendered; round 2: the round 1 fix only lifted the cap one level up,
    // so the identical bug reappeared two-or-more levels above the ticked
    // node, since RecomputeFromChildren bubbles state all the way up the
    // ancestor chain). Before patching it a third time, a throwaway
    // benchmark measured what the cap actually bought: inserting 10,000
    // flat TreeNodes via BeginUpdate/EndUpdate took ~39ms, and a full-tree
    // appearance refresh over 10,000 nodes took ~14ms - both roughly two
    // orders of magnitude under "still feels instant", and 10,000 is far
    // beyond the widest real directory found on the reference/dev machines
    // (259-447 entries). The cap was not earning its keep, so
    // EnsureChildrenLoaded now always loads every entry once a directory is
    // expanded - see its doc comment and the task report's "Fix round 2"
    // section for the full numbers - and the three tests above, which
    // existed solely to pin down cap-specific behaviour, no longer have
    // anything to assert; they were deleted rather than left checking
    // nothing meaningful.
    //
    // What replaces them: with no cap, "does a deep tick/untick correctly
    // cover its whole subtree" is just the general Checked/Indeterminate
    // bubbling behaviour, now exercised at TWO levels of nesting - the
    // depth at which round 2's regression specifically lived.
    [Fact]
    public void DeepUntickTwoLevelsBelowATickedAncestorStillCoversEverySibling()
    {
        WriteFile("skills/sub/g0.txt");
        WriteFile("skills/sub/g1.txt");
        WriteFile("skills/a.txt");
        WriteFile("skills/b.txt");
        WriteFile("skills/c.txt");

        var model = new BackupTreeModel(_root);
        model.EnsureChildrenLoaded(model.Root);
        var skills = model.Root.Children.Single(c => c.Name == "skills");

        Assert.True(model.SetChecked(skills, true));
        Assert.Equal(new[] { "skills/**" }, model.GeneratePatterns());

        // Expand two levels down and untick just one grandchild.
        model.EnsureChildrenLoaded(skills);
        var sub = skills.Children.Single(c => c.Name == "sub");
        model.EnsureChildrenLoaded(sub);
        var g0 = sub.Children.Single(c => c.Name == "g0.txt");

        Assert.True(model.SetChecked(g0, false));

        Assert.Equal(NodeCheckState.Indeterminate, sub.CheckState);
        Assert.Equal(NodeCheckState.Indeterminate, skills.CheckState);

        // Nothing was silently dropped: every sibling at every level except
        // the one explicitly unticked file is still present.
        var patterns = model.GeneratePatterns().ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("skills/**", patterns);
        Assert.DoesNotContain("skills/sub/g0.txt", patterns);
        Assert.Contains("skills/sub/g1.txt", patterns);
        Assert.Contains("skills/a.txt", patterns);
        Assert.Contains("skills/b.txt", patterns);
        Assert.Contains("skills/c.txt", patterns);
        Assert.Equal(4, patterns.Count);

        // Re-ticking the one file recovers the full, single-pattern selection.
        Assert.True(model.SetChecked(g0, true));
        Assert.Equal(new[] { "skills/**" }, model.GeneratePatterns());
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

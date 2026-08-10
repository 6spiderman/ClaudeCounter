using System.Text;
using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// The file picker: a lazily-populated, checkbox TreeView over one backup
/// destination's SourceRoot, with the resulting Include patterns shown
/// read-only underneath. A thin WinForms adapter over <see
/// cref="BackupTreeModel"/> - all the actual selection/mapping logic lives
/// there and is unit tested independently; this class only wires that model
/// to TreeView events and paints labels. Never construct this from a test
/// except via SettingsFormSmokeTests' dedicated STA helper.
///
/// Opened from the Backup tab's "Choose files..." button, once per
/// destination - the caller passes which destination it is editing so the
/// title can say so (GitHub and Drive have fully independent selections;
/// this dialog only ever edits one of them at a time).
/// </summary>
public sealed class BackupPickerDialog : Form
{
    private const int DialogWidth = 560;
    private const int Pad = 16;
    private const int TreeHeight = 300;
    private const int PatternsBoxHeight = 130;
    private const int BottomBarHeight = 52;

    // Fix round 1, Important 2: caps how many directory-size walks
    // (ClaudeLocationScanner.ComputeStats) run at once - without this,
    // expanding a wide directory queues up to ~500 concurrent full-subtree
    // walks. Cancelled as a whole via _cts when the dialog closes, so a walk
    // for a node the user is no longer even looking at does not keep
    // running to completion in the background.
    private const int MaxConcurrentSizeComputations = 4;

    private readonly Palette _palette;
    private readonly BackupTreeModel _model;
    private readonly TreeView _tree;
    private readonly TextBox _patternsBox;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sizeThrottle = new(MaxConcurrentSizeComputations);

    // Guards against BuildTreeNode/PopulateChildren/RefreshWholeTreeAppearance's
    // own programmatic TreeNode.Checked assignments re-entering OnAfterCheck -
    // without this, ticking one node would recurse through every cascaded
    // sibling/ancestor change as if the user had clicked each one individually.
    private bool _suppressCheckEvents;

    // Fix round 1, Important 1: the root's children (and their size
    // computations) are populated from OnHandleCreated, not the
    // constructor - see that override's doc comment. This guards against
    // running that initialization twice (OnHandleCreated can, in principle,
    // fire again if the handle is recreated).
    private bool _initialized;

    /// <summary>The Include pattern list for the current selection - always in sync (recomputed after every tick), not only once OK is pressed.</summary>
    public IReadOnlyList<string> Include => _model.GeneratePatterns();

    public BackupPickerDialog(Palette palette, string destinationName, string sourceRoot, IReadOnlyList<string> currentInclude)
    {
        _palette = palette;
        _model = BackupTreeModel.LoadFromPatterns(sourceRoot, currentInclude);

        Text = $"Choose files for {destinationName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = palette.Back;
        ForeColor = palette.Fore;
        KeyPreview = true;

        var y = Pad;

        var note = new Label
        {
            Text = $"Editing the file selection for {destinationName}. Ticking a folder includes " +
                   "everything in it, even entries not shown below. Denylisted entries (greyed) can " +
                   "never be selected.",
            AutoSize = true,
            MaximumSize = new Size(DialogWidth - Pad * 2, 0),
            Font = Font,
            ForeColor = palette.SubtleFore,
            BackColor = Color.Transparent,
            Location = new Point(Pad, y),
        };
        Controls.Add(note);
        y += note.PreferredHeight + 8;

        _tree = new TreeView
        {
            CheckBoxes = true,
            ShowNodeToolTips = true,
            Location = new Point(Pad, y),
            Size = new Size(DialogWidth - Pad * 2, TreeHeight),
            Font = Font,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _tree.BeforeExpand += OnBeforeExpand;
        _tree.BeforeCheck += OnBeforeCheck;
        _tree.AfterCheck += OnAfterCheck;
        Controls.Add(_tree);
        y += _tree.Height + 8;

        var patternsLabel = new Label
        {
            Text = "Generated Include patterns",
            AutoSize = true,
            Font = Font,
            ForeColor = palette.Fore,
            BackColor = Color.Transparent,
            Location = new Point(Pad, y),
        };
        Controls.Add(patternsLabel);
        y += patternsLabel.PreferredHeight + 2;

        _patternsBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(Pad, y),
            Size = new Size(DialogWidth - Pad * 2, PatternsBoxHeight),
            Font = Font,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(_patternsBox);
        y += _patternsBox.Height + 8;

        // Fix round 1, Important 4: ClaudeLocationScanner is fully written
        // and tested but was otherwise never called from shipping code - and
        // more importantly, a user who cannot see ~/.claude.json (the file
        // most people think of as "their Claude config") anywhere in this
        // dialog could easily assume it is already covered when it is not.
        // The engine only ever walks ONE root (SourceRoot), so these other
        // locations are not YET selectable here - but staying silent about
        // them is worse than saying so plainly.
        var otherLocations = ClaudeLocationScanner.Probe()
            .Where(l => !PathsEqual(l.Path, sourceRoot))
            .ToList();
        if (otherLocations.Count > 0)
        {
            var otherLabel = new Label
            {
                Text = "Other Claude locations found (not yet selectable here): " +
                       string.Join(", ", otherLocations.Select(l => l.DisplayName)),
                AutoSize = true,
                MaximumSize = new Size(DialogWidth - Pad * 2, 0),
                Font = Font,
                ForeColor = palette.SubtleFore,
                BackColor = Color.Transparent,
                Location = new Point(Pad, y),
            };
            Controls.Add(otherLabel);
            y += otherLabel.PreferredHeight + 8;
        }

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = palette.BarBack };
        bottomBar.Controls.Add(new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(DialogWidth, 1),
            BackColor = palette.Border,
        });
        var cancelButton = NewDialogButton("Cancel", palette);
        cancelButton.DialogResult = DialogResult.Cancel;
        var okButton = NewDialogButton("OK", palette);
        okButton.DialogResult = DialogResult.OK;
        var cancelX = DialogWidth - Pad - cancelButton.Width;
        var okX = cancelX - 8 - okButton.Width;
        var btnY = (BottomBarHeight - okButton.Height) / 2;
        cancelButton.Location = new Point(cancelX, btnY);
        okButton.Location = new Point(okX, btnY);
        bottomBar.Controls.Add(cancelButton);
        bottomBar.Controls.Add(okButton);
        Controls.Add(bottomBar);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        ClientSize = new Size(DialogWidth, y + BottomBarHeight);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        };
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static Button NewDialogButton(string text, Palette palette)
    {
        var button = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat,
            BackColor = palette.BarBack,
            ForeColor = palette.Fore,
            Size = new Size(84, 28),
        };
        button.FlatAppearance.BorderColor = palette.Border;
        return button;
    }

    /// <summary>
    /// Fix round 1, Important 1: populating the root's children (and
    /// kicking off their off-thread size computations) used to happen
    /// directly in the constructor. StartSizeComputation's own guard against
    /// updating a closed dialog checks IsHandleCreated before calling
    /// BeginInvoke - but the constructor runs before ShowDialog (or a
    /// test's own `_ = form.Handle`) has ever created that handle, so for a
    /// small directory whose ComputeStats finishes in microseconds, the
    /// check would see IsHandleCreated still false and skip the UI update
    /// forever - the label would be stuck on "..." with no further trigger
    /// to ever refresh it. Doing this from OnHandleCreated instead
    /// guarantees the handle already exists for every size computation this
    /// dialog ever starts.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_initialized)
            return;
        _initialized = true;

        _model.EnsureChildrenLoaded(_model.Root);
        PopulateChildren(_tree.Nodes, _model.Root);
        RefreshPatternsBox();
    }

    private void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node?.Tag is not FileTreeNode node || node.ChildrenLoaded)
            return;
        _model.EnsureChildrenLoaded(node);
        PopulateChildren(e.Node.Nodes, node);
    }

    /// <summary>
    /// Refuses the check for anything that is not a real, selectable node -
    /// a denylisted entry (Tag is a FileTreeNode with IsDenylisted true) or
    /// the "... and N more (not shown)" placeholder (Tag is null) - so the
    /// picker cannot offer any route around the denylist and the cap
    /// placeholder cannot be mistaken for a real, tickable entry.
    /// </summary>
    private void OnBeforeCheck(object? sender, TreeViewCancelEventArgs e)
    {
        if (_suppressCheckEvents)
            return;
        if (e.Node?.Tag is not FileTreeNode node || node.IsDenylisted)
            e.Cancel = true;
    }

    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suppressCheckEvents)
            return;
        if (e.Node?.Tag is not FileTreeNode node)
            return;

        // BackupTreeModel.SetChecked may need to fully load a display-capped
        // parent before it can apply this tick correctly (fix round 1,
        // CRITICAL - see BackupTreeModel.EnsureFullyLoaded) - that is one
        // more Directory.GetDirectories/GetFiles call, not the recursive
        // size walk, so it is fast, but a wait cursor costs nothing and
        // avoids the pointer looking idle for however long that takes on a
        // very wide directory.
        Cursor = Cursors.WaitCursor;
        try
        {
            _model.SetChecked(node, e.Node.Checked);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        // A tick can also lift the display cap on node's parent entirely
        // (HiddenChildCount permanently drops to 0) - keep the "... and N
        // more" placeholder, if any, in sync with that rather than leaving
        // it showing a stale count (or claiming entries are hidden when
        // they no longer are).
        SyncCapPlaceholder(e.Node.Parent?.Nodes ?? _tree.Nodes, node.Parent ?? _model.Root);

        // A single tick can cascade through the whole loaded tree (children
        // forced to match, ancestors recomputed) - re-sync every
        // materialized TreeNode from the model rather than trying to track
        // which ones changed.
        RefreshWholeTreeAppearance();
        RefreshPatternsBox();
    }

    private void SyncCapPlaceholder(TreeNodeCollection uiCollection, FileTreeNode modelParent)
    {
        TreeNode? placeholder = null;
        foreach (TreeNode candidate in uiCollection)
        {
            if (candidate.Tag is null && candidate.Text.StartsWith("... and ", StringComparison.Ordinal))
            {
                placeholder = candidate;
                break;
            }
        }
        if (placeholder is null)
            return;

        if (modelParent.HiddenChildCount == 0)
            uiCollection.Remove(placeholder);
        else
            placeholder.Text = $"... and {modelParent.HiddenChildCount} more (not shown)";
    }

    private void PopulateChildren(TreeNodeCollection uiCollection, FileTreeNode modelParent)
    {
        _suppressCheckEvents = true;
        try
        {
            uiCollection.Clear();
            foreach (var child in modelParent.Children)
                uiCollection.Add(BuildTreeNode(child));

            if (modelParent.HiddenChildCount > 0)
            {
                uiCollection.Add(new TreeNode($"... and {modelParent.HiddenChildCount} more (not shown)")
                {
                    ForeColor = _palette.SubtleFore,
                    ToolTipText = "Display limit only - ticking the parent folder still includes these, " +
                                  "and ticking any individually-shown sibling loads the rest too.",
                });
            }
        }
        finally
        {
            _suppressCheckEvents = false;
        }
    }

    private TreeNode BuildTreeNode(FileTreeNode child)
    {
        var uiNode = new TreeNode { Tag = child };

        // Set once at creation - unlike Text/Checked/ForeColor (see
        // RefreshNodeAppearance), neither of these ever changes for the
        // lifetime of the node.
        if (child.IsDenylisted)
            uiNode.ToolTipText = "Denylisted - " + child.DenylistReason;
        else if (child.IsTranscriptBearing)
            uiNode.ToolTipText = child.TranscriptNote;

        RefreshNodeAppearance(uiNode, child);

        if (child.IsDirectory)
        {
            // Placeholder child so the [+] expand glyph shows before the
            // real contents are ever listed - replaced by PopulateChildren
            // the first time this node is expanded (see OnBeforeExpand).
            uiNode.Nodes.Add(new TreeNode());
            if (child.SizeBytes is null)
                StartSizeComputation(child, uiNode);
        }

        return uiNode;
    }

    /// <summary>
    /// Fix round 1, Important 2: the directory-size walk
    /// (ClaudeLocationScanner.ComputeStats) is throttled to at most
    /// MaxConcurrentSizeComputations concurrent walks (a SemaphoreSlim) and
    /// fully cancellable via _cts, cancelled in Dispose - without this,
    /// closing the dialog left every already-queued or in-flight walk
    /// running to completion regardless (the old IsDisposed check only ever
    /// suppressed the resulting UI update, not the walk itself), and
    /// expanding one wide directory could queue up to ~500 of them at once.
    ///
    /// Fix round 1, Minor: node.SizeBytes/FileCount are now written only
    /// inside the BeginInvoke lambda (i.e. on the UI thread), not on the
    /// background thread beforehand - writing them from the background
    /// thread and reading them from the UI thread with no synchronization
    /// risked a concurrent refresh observing SizeBytes and FileCount at
    /// different points in time (each nullable's HasValue/Value assignment
    /// is not atomic together).
    /// </summary>
    private void StartSizeComputation(FileTreeNode node, TreeNode uiNode)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await _sizeThrottle.WaitAsync(token).ConfigureAwait(false);
                acquired = true;

                var stats = ClaudeLocationScanner.ComputeStats(node.FullPath, isDirectory: true, token);

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed)
                                return;
                            node.SizeBytes = stats.TotalBytes;
                            node.FileCount = stats.FileCount;
                            uiNode.Text = FormatLabel(node);
                        }));
                }
                catch (ObjectDisposedException) { /* dialog closed mid-flight */ }
                catch (InvalidOperationException) { /* handle destroyed mid-flight */ }
            }
            catch (OperationCanceledException)
            {
                // Dialog closed (either while waiting for a throttle slot, or
                // mid-walk) - nothing left to compute or show.
            }
            finally
            {
                if (acquired)
                    _sizeThrottle.Release();
            }
        }, token);
    }

    private void RefreshWholeTreeAppearance()
    {
        _suppressCheckEvents = true;
        try
        {
            RefreshRecursive(_tree.Nodes);
        }
        finally
        {
            _suppressCheckEvents = false;
        }
    }

    private void RefreshRecursive(TreeNodeCollection nodes)
    {
        foreach (TreeNode uiNode in nodes)
        {
            if (uiNode.Tag is FileTreeNode node)
                RefreshNodeAppearance(uiNode, node);
            RefreshRecursive(uiNode.Nodes);
        }
    }

    /// <summary>
    /// Fix round 1, Minor: previously only Checked and Text were kept in
    /// sync on every refresh - ForeColor was set once at creation and never
    /// revisited, so a node that became Indeterminate after this dialog
    /// opened never actually looked any different from a fully Unchecked
    /// one (the review's own finding: "a user cannot currently distinguish
    /// none-from-some at a glance"). Indeterminate nodes now get a distinct
    /// color (the same amber used elsewhere in this app for a warn-level
    /// state - Theme.BandColor(Band.Amber) - reused rather than inventing a
    /// new one), and this recomputes it every refresh, not just at creation,
    /// so a node moving between Unchecked/Checked/Indeterminate always
    /// matches its current state.
    /// </summary>
    private void RefreshNodeAppearance(TreeNode uiNode, FileTreeNode node)
    {
        uiNode.Text = FormatLabel(node);
        uiNode.Checked = node.CheckState == NodeCheckState.Checked;
        uiNode.ForeColor = node.IsDenylisted
            ? _palette.SubtleFore
            : node.CheckState == NodeCheckState.Indeterminate
                ? Theme.BandColor(Band.Amber)
                : _palette.Fore;
    }

    private void RefreshPatternsBox()
    {
        var all = _model.GeneratePatterns();
        var unrepresentable = _model.UnrepresentablePatterns;
        var representable = all.Except(unrepresentable).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Generated from selection:");
        if (representable.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var p in representable)
            sb.AppendLine("  " + p);

        if (unrepresentable.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not representable in the tree (preserved as-is):");
            foreach (var p in unrepresentable)
                sb.AppendLine("  " + p);
        }

        _patternsBox.Text = sb.ToString().TrimEnd();
    }

    /// <summary>
    /// "name[ (partial)] (stat)[ [contains chat transcripts]]" - fix round 1,
    /// Minor: the partial flag moved from the end to immediately after the
    /// name (it used to land after the size stat, e.g.
    /// "projects (622 files, 340.7 MB) (partial) [...]", far from the
    /// checkbox it is meant to explain). The stat segment is "..." for a
    /// directory whose size has not arrived yet (see StartSizeComputation),
    /// and the transcript flag is a visible label suffix (not just a hover
    /// tooltip) per the design spec.
    /// </summary>
    private static string FormatLabel(FileTreeNode node)
    {
        var partial = node.CheckState == NodeCheckState.Indeterminate ? " (partial)" : "";
        var stat = BuildStatText(node);
        var statPart = stat.Length == 0 ? "" : $" ({stat})";
        var transcript = node.IsTranscriptBearing ? " [contains chat transcripts]" : "";

        return $"{node.Name}{partial}{statPart}{transcript}";
    }

    private static string BuildStatText(FileTreeNode node)
    {
        if (!node.IsDirectory)
            return node.SizeBytes is { } fileSize ? FormatSize(fileSize) : "";

        if (node.FileCount is not { } count || node.SizeBytes is not { } dirSize)
            return "...";
        return $"{count} file{(count == 1 ? "" : "s")}, {FormatSize(dirSize)}";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{(long)size} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }

    // _cts/_sizeThrottle are not in the Controls tree, so nothing else
    // disposes them - mirrors SettingsForm's own Dispose override for
    // _helpTip. Cancelling first ensures any task still waiting on the
    // semaphore (or mid-walk, via the token threaded into ComputeStats)
    // unwinds promptly rather than racing the semaphore's own disposal.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            _sizeThrottle.Dispose();
        }
        base.Dispose(disposing);
    }
}

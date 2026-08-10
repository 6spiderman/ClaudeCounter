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

    private readonly Palette _palette;
    private readonly BackupTreeModel _model;
    private readonly TreeView _tree;
    private readonly TextBox _patternsBox;

    // Guards against BuildTreeNode/PopulateChildren/RefreshWholeTreeAppearance's
    // own programmatic TreeNode.Checked assignments re-entering OnAfterCheck -
    // without this, ticking one node would recurse through every cascaded
    // sibling/ancestor change as if the user had clicked each one individually.
    private bool _suppressCheckEvents;

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

        // Root's immediate children are loaded eagerly (a single, cheap
        // directory listing) so the picker opens already showing something -
        // everything deeper stays lazy (see OnBeforeExpand), and directory
        // sizes are still computed off-thread (see StartSizeComputation).
        _model.EnsureChildrenLoaded(_model.Root);
        PopulateChildren(_tree.Nodes, _model.Root);
        RefreshPatternsBox();
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

        _model.SetChecked(node, e.Node.Checked);
        // A single tick can cascade through the whole loaded tree (children
        // forced to match, ancestors recomputed) - re-sync every
        // materialized TreeNode from the model rather than trying to track
        // which ones changed.
        RefreshWholeTreeAppearance();
        RefreshPatternsBox();
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
                    ToolTipText = "Display limit only - ticking the parent folder still includes these.",
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
        ApplyAppearance(uiNode, child);

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

    private void ApplyAppearance(TreeNode uiNode, FileTreeNode node)
    {
        uiNode.Text = FormatLabel(node);
        uiNode.Checked = node.CheckState == NodeCheckState.Checked;

        if (node.IsDenylisted)
        {
            uiNode.ForeColor = _palette.SubtleFore;
            uiNode.ToolTipText = "Denylisted - " + node.DenylistReason;
        }
        else if (node.IsTranscriptBearing)
        {
            uiNode.ToolTipText = node.TranscriptNote;
        }
    }

    /// <summary>
    /// Computes a directory's file count and total size off the UI thread
    /// (see ClaudeLocationScanner.ComputeStats - this can walk thousands of
    /// files, e.g. skills/ on the reference machine) and updates the node's
    /// label once ready. Guards against the dialog having been closed while
    /// the background computation was still running.
    /// </summary>
    private void StartSizeComputation(FileTreeNode node, TreeNode uiNode)
    {
        Task.Run(() => ClaudeLocationScanner.ComputeStats(node.FullPath, isDirectory: true))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    return;
                node.SizeBytes = t.Result.TotalBytes;
                node.FileCount = t.Result.FileCount;
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(() =>
                        {
                            if (!IsDisposed)
                                uiNode.Text = FormatLabel(node);
                        }));
                }
                catch (ObjectDisposedException) { /* dialog closed mid-flight */ }
                catch (InvalidOperationException) { /* handle destroyed mid-flight */ }
            }, TaskScheduler.Default);
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
            {
                uiNode.Checked = node.CheckState == NodeCheckState.Checked;
                uiNode.Text = FormatLabel(node);
            }
            RefreshRecursive(uiNode.Nodes);
        }
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
    /// "name (stat)[ (partial)][ [contains chat transcripts]]" - the stat
    /// segment is "..." for a directory whose size has not arrived yet (see
    /// StartSizeComputation), the partial flag reflects Indeterminate (the
    /// checkbox itself is only two-state), and the transcript flag is a
    /// visible label suffix (not just a hover tooltip) per the design spec.
    /// </summary>
    private static string FormatLabel(FileTreeNode node)
    {
        var stat = BuildStatText(node);
        var flags = "";
        if (node.CheckState == NodeCheckState.Indeterminate)
            flags += " (partial)";
        if (node.IsTranscriptBearing)
            flags += " [contains chat transcripts]";

        return stat.Length == 0 ? node.Name + flags : $"{node.Name} ({stat}){flags}";
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
}

using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// The file picker: a lazily-populated TreeView with a three-state checkbox
/// per node over one backup destination's SourceRoot, with the resulting
/// Include patterns shown read-only underneath. The Linux port of the Windows
/// build's BackupPickerDialog and, like it, a thin adapter over the shared
/// <see cref="BackupTreeModel"/> - all the selection/mapping logic lives there
/// (and is unit tested there); this class only wires that model to TreeView
/// events and paints labels.
///
/// Opened once per destination from "Choose files..." and shown modally with
/// <c>await picker.ShowDialog&lt;bool&gt;(owner)</c>: true on OK, false on
/// Cancel, Esc or the window's close button. The caller reads
/// <see cref="Include"/> when the result is true.
///
/// Same loading strategy as Windows, so a big folder never freezes the UI:
/// a directory's entries are listed one level at a time, only when it is
/// first expanded (a single directory listing, not a walk), and each
/// directory's recursive file count and size are computed on background
/// threads - at most <see cref="MaxConcurrentSizeComputations"/> at once,
/// all cancelled when the dialog closes - and posted back to the label.
/// </summary>
public sealed class BackupPickerDialog : Window
{
    private const double DialogWidth = 560;
    private const double TreeHeight = 300;
    private const double PatternsBoxHeight = 130;

    // Caps how many directory-size walks (ClaudeLocationScanner.ComputeStats)
    // run at once - expanding a wide directory would otherwise queue hundreds
    // of concurrent full-subtree walks. Same limit as the Windows dialog.
    private const int MaxConcurrentSizeComputations = 4;

    private readonly BackupTreeModel _model;
    private readonly TreeView _tree;
    private readonly TextBox _patternsBox;
    private readonly IBrush _indeterminateBrush = new SolidColorBrush(BandPalette.BandColor(Band.Amber));
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sizeThrottle = new(MaxConcurrentSizeComputations);

    // Every row materialized so far. Nodes are only ever added (a directory
    // is listed once), so a flat list is all a whole-tree refresh needs.
    private readonly List<NodeRow> _rows = new();

    private bool _initialized;
    private bool _closed;

    /// <summary>The Include pattern list for the current selection - always in sync (recomputed after every tick), not only once OK is pressed.</summary>
    public IReadOnlyList<string> Include => _model.GeneratePatterns();

    public BackupPickerDialog(string destinationName, string sourceRoot, IReadOnlyList<string> currentInclude)
    {
        _model = BackupTreeModel.LoadFromPatterns(sourceRoot, currentInclude);

        Title = $"Choose files for {destinationName}";
        Width = DialogWidth;
        // Height follows the content (fixed-height tree and pattern boxes
        // inside), so wrapped text never clips the OK/Cancel row.
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 8 };

        root.Children.Add(Hint(
            $"Editing the file selection for {destinationName}. Ticking a folder includes " +
            "everything in it. Denylisted entries (greyed) can never be selected."));

        _tree = new TreeView
        {
            Height = TreeHeight,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
        };
        // Space ticks the selected row, as with a WinForms checkbox TreeView.
        // Tunnelling, so TreeView's own key handling cannot swallow it first.
        _tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        root.Children.Add(_tree);

        root.Children.Add(new TextBlock { Text = "Generated Include patterns", Margin = new Thickness(0, 4, 0, 0) });

        _patternsBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = PatternsBoxHeight,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        root.Children.Add(_patternsBox);

        // The engine only ever walks ONE root (SourceRoot), so other Claude
        // locations are not selectable here - but a user who cannot see
        // ~/.claude.json anywhere could easily assume it is already covered,
        // so say plainly that it is not. Probe() only checks existence.
        var otherLocations = ClaudeLocationScanner.Probe()
            .Where(l => !PathsEqual(l.Path, sourceRoot))
            .ToList();
        if (otherLocations.Count > 0)
        {
            root.Children.Add(Hint(
                "Other Claude locations found (not yet selectable here): " +
                string.Join(", ", otherLocations.Select(l => l.DisplayName))));
        }

        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        okButton.Click += (_, _) => Close(true);
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancelButton.Click += (_, _) => Close(false);

        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { cancelButton, okButton },
        });

        Content = root;

        // Listing the root and starting its size walks waits until the
        // window is open (the Windows dialog does this from OnHandleCreated),
        // so a dialog that is constructed but never shown starts no
        // background work that nothing would ever cancel.
        Opened += (_, _) => Initialize();

        // Never cancels a close (an Avalonia window refusing to close would
        // block a desktop logout); it only stops the background size walks.
        Closed += (_, _) =>
        {
            _closed = true;
            _cts.Cancel();
            _cts.Dispose();
            // _sizeThrottle is deliberately not disposed: a walk that is
            // already past its cancellation check still Release()s it on the
            // way out, and a SemaphoreSlim whose AvailableWaitHandle is never
            // touched owns nothing that needs disposing.
        };
    }

    private void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        _model.EnsureChildrenLoaded(_model.Root);
        PopulateChildren(_tree.Items, _model.Root);
        RefreshPatternsBox();
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
    };

    // Linux paths are case-sensitive, so compare ordinally (the Windows
    // dialog compares case-insensitively, as Windows paths are).
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void PopulateChildren(ItemCollection uiCollection, FileTreeNode modelParent)
    {
        uiCollection.Clear();
        foreach (var child in modelParent.Children)
            uiCollection.Add(BuildTreeItem(child));
    }

    private TreeViewItem BuildTreeItem(FileTreeNode node)
    {
        var check = new PickerCheckBox
        {
            IsThreeState = true,
            // Keyboard focus stays on the tree row (Space toggles it, see
            // OnTreeKeyDown), so the checkbox itself is click-only.
            Focusable = false,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            // The picker must not offer any route around the denylist.
            IsEnabled = !node.IsDenylisted,
        };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { check, label },
        };

        // Set once - unlike the label/check/colour (RefreshRow), neither ever
        // changes for the lifetime of the node.
        if (node.IsDenylisted)
            ToolTip.SetTip(header, "Denylisted - " + node.DenylistReason);
        else if (node.IsTranscriptBearing)
            ToolTip.SetTip(header, node.TranscriptNote);

        var item = new TreeViewItem { Header = header };
        var row = new NodeRow(node, item, check, label);
        item.Tag = row;
        _rows.Add(row);

        check.UserToggled += () => ToggleRow(row);
        RefreshRow(row);

        if (node.IsDirectory)
        {
            // Placeholder child so the expander glyph shows before the real
            // contents are ever listed - replaced the first time this node
            // is expanded.
            item.Items.Add(new TreeViewItem());
            item.Expanded += (_, e) =>
            {
                // Expanded bubbles; only react to this item's own expansion.
                if (!ReferenceEquals(e.Source, item) || node.ChildrenLoaded)
                    return;
                _model.EnsureChildrenLoaded(node);
                PopulateChildren(item.Items, node);
            };

            if (node.SizeBytes is null)
                StartSizeComputation(row);
        }

        return item;
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || _tree.SelectedItem is not TreeViewItem { Tag: NodeRow row })
            return;
        ToggleRow(row);
        e.Handled = true;
    }

    /// <summary>
    /// A tick or untick from the user. Unchecked and partial both become
    /// Checked (a partial row looked unticked on Windows, so a click ticked
    /// it); Checked becomes Unchecked. The checkbox never changes itself -
    /// <see cref="PickerCheckBox"/> only reports the click - so the box always
    /// shows exactly what the model decided.
    /// </summary>
    private void ToggleRow(NodeRow row)
    {
        if (row.Node.IsDenylisted)
            return;

        _model.SetChecked(row.Node, row.Node.CheckState != NodeCheckState.Checked);

        // A single tick can cascade through the whole loaded tree (children
        // forced to match, ancestors recomputed) - re-sync every
        // materialized row from the model rather than tracking which changed.
        foreach (var r in _rows)
            RefreshRow(r);
        RefreshPatternsBox();
    }

    /// <summary>
    /// Directory-size walk off the UI thread, throttled to
    /// <see cref="MaxConcurrentSizeComputations"/> and cancelled when the
    /// dialog closes. The node's size and count are written only on the UI
    /// thread, so a refresh never sees one updated without the other.
    /// </summary>
    private void StartSizeComputation(NodeRow row)
    {
        var node = row.Node;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await _sizeThrottle.WaitAsync(token).ConfigureAwait(false);
                acquired = true;

                var stats = ClaudeLocationScanner.ComputeStats(node.FullPath, isDirectory: true, token);

                Dispatcher.UIThread.Post(() =>
                {
                    if (_closed)
                        return;
                    node.SizeBytes = stats.TotalBytes;
                    node.FileCount = stats.FileCount;
                    row.Label.Text = FormatLabel(node);
                });
            }
            catch (OperationCanceledException)
            {
                // Dialog closed (while waiting for a slot, or mid-walk) -
                // nothing left to compute or show.
            }
            catch (Exception ex)
            {
                Log.Warn($"BackupPickerDialog: size computation for '{node.FullPath}' failed: {ex.Message}");
            }
            finally
            {
                if (acquired)
                    _sizeThrottle.Release();
            }
        }, token);
    }

    /// <summary>
    /// Re-syncs one row from the model: label text, the checkbox (null =
    /// indeterminate), and the colour - greyed for denylisted, the app's amber
    /// warn colour for partially selected (as on Windows), otherwise the
    /// theme's normal text colour.
    /// </summary>
    private void RefreshRow(NodeRow row)
    {
        var node = row.Node;
        row.Label.Text = FormatLabel(node);
        row.Check.IsChecked = node.CheckState switch
        {
            NodeCheckState.Checked => true,
            NodeCheckState.Indeterminate => null,
            _ => false,
        };

        if (node.IsDenylisted)
            row.Label.Foreground = Brushes.Gray;
        else if (node.CheckState == NodeCheckState.Indeterminate)
            row.Label.Foreground = _indeterminateBrush;
        else
            row.Label.ClearValue(TextBlock.ForegroundProperty);
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
    /// "name[ (partial)] (stat)[ [contains chat transcripts]]" - identical to
    /// the Windows dialog. The stat is "..." for a directory whose size has
    /// not arrived yet (see StartSizeComputation).
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

    private sealed record NodeRow(FileTreeNode Node, TreeViewItem Item, PickerCheckBox Check, TextBlock Label);

    /// <summary>
    /// A three-state CheckBox that reports a click instead of cycling its own
    /// state: the next state depends on the model (a partial row becomes
    /// Checked, not Unchecked as a plain three-state cycle would make it), so
    /// the dialog decides and writes IsChecked back.
    /// </summary>
    private sealed class PickerCheckBox : CheckBox
    {
        public event Action? UserToggled;

        protected override Type StyleKeyOverride => typeof(CheckBox);

        protected override void Toggle() => UserToggled?.Invoke();
    }
}

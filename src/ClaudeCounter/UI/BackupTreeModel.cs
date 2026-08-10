using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>Tri-state selection, mirroring a WinForms TreeView checkbox's three visual states.</summary>
public enum NodeCheckState
{
    Unchecked,
    Checked,
    Indeterminate,
}

/// <summary>
/// One node of the file picker's tree - a pure data/logic type with no
/// WinForms dependency, so it (and <see cref="BackupTreeModel"/>) can be unit
/// tested without ever constructing a Form or a TreeView. BackupPickerDialog
/// is the thin WinForms adapter that turns this into an actual TreeView.
///
/// Lazily populated: a directory node's <see cref="Children"/> stays empty
/// until <see cref="BackupTreeModel.EnsureChildrenLoaded"/> is called for it
/// (on first expand, in the real dialog) - constructing a node, even the
/// root, never walks its subtree.
/// </summary>
public sealed class FileTreeNode
{
    /// <summary>
    /// Transcript-bearing top-level entries (see the design spec) - selectable,
    /// but the picker must say plainly that they hold what was typed and file
    /// contents. Only checked at depth 1 (an immediate child of the root):
    /// depth is what "top-level entry under ~/.claude" means, and a
    /// coincidentally same-named folder nested somewhere else is not this.
    /// </summary>
    private static readonly HashSet<string> TranscriptBearingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "projects", "file-history", "history.jsonl", "shell-snapshots", "telemetry",
    };

    private readonly List<FileTreeNode> _children = new();

    public string Name { get; }

    /// <summary>Forward-slash path relative to the tree's SourceRoot; empty for the root node itself.</summary>
    public string RelativePath { get; }

    public string FullPath { get; }
    public bool IsDirectory { get; }
    public FileTreeNode? Parent { get; }

    public NodeCheckState CheckState { get; internal set; } = NodeCheckState.Unchecked;

    /// <summary>True when <see cref="SecretDenylist.IsSecret"/> matches this node's name - shown greyed, never tickable.</summary>
    public bool IsDenylisted { get; }
    public string? DenylistReason { get; }

    public bool IsTranscriptBearing { get; }
    public string? TranscriptNote { get; }

    public bool ChildrenLoaded { get; internal set; }
    public IReadOnlyList<FileTreeNode> Children => _children;

    /// <summary>
    /// The real number of entries in this directory, before the picker's
    /// ~500-child display cap trims <see cref="Children"/>. Ticking THIS
    /// node still selects everything under it (dir/**), including whatever
    /// was left out of Children by the cap - see BackupTreeModel.SetChecked.
    /// </summary>
    public int TotalChildCount { get; internal set; }

    public int HiddenChildCount => Math.Max(0, TotalChildCount - _children.Count);

    /// <summary>Byte size, or null when not yet computed (directories start unknown; see BackupTreeModel.EnsureChildrenLoaded's doc comment).</summary>
    public long? SizeBytes { get; internal set; }

    /// <summary>File count, or null when not yet computed.</summary>
    public int? FileCount { get; internal set; }

    internal FileTreeNode(string name, string relativePath, string fullPath, bool isDirectory, FileTreeNode? parent)
    {
        Name = name;
        RelativePath = relativePath;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;

        // SecretDenylist.IsSecret looks at Path.GetFileName internally, so
        // passing the bare name (rather than the full relative path) is
        // equivalent and avoids any ambiguity for the root node, whose
        // RelativePath is "".
        IsDenylisted = SecretDenylist.IsSecret(name);
        if (IsDenylisted)
            DenylistReason = "never backed up: credential material";

        IsTranscriptBearing = parent is { RelativePath: "" } && TranscriptBearingNames.Contains(name);
        if (IsTranscriptBearing)
            TranscriptNote = "Contains conversation transcripts and file contents shared with Claude.";
    }

    internal void AddChild(FileTreeNode child) => _children.Add(child);

    /// <summary>
    /// Sets this node's own state and cascades the same state onto every
    /// currently-loaded, non-denylisted descendant - a denylisted node is
    /// skipped (it can never be ticked) but its own children (if any -
    /// practically never, since denylisted entries here are files) are still
    /// walked so cascading through it does not stop early.
    /// </summary>
    internal void ApplyCheckedRecursively(NodeCheckState state)
    {
        if (!IsDenylisted)
            CheckState = state;
        foreach (var child in _children)
            child.ApplyCheckedRecursively(state);
    }

    /// <summary>
    /// Recomputes this node's own CheckState from its currently-loaded
    /// children, then bubbles the same recomputation up to the parent. Only
    /// promotes to fully Checked when every loaded, selectable child is
    /// Checked AND nothing was left out by the display cap
    /// (<see cref="HiddenChildCount"/> is 0) - otherwise "check every
    /// visible child" could quietly imply "and everything I couldn't show
    /// you too", which is backwards: only an explicit tick on this node
    /// itself (see BackupTreeModel.SetChecked) is allowed to mean that.
    /// Denylisted children are excluded from the vote entirely - a
    /// directory containing only a denylisted file should not be dragged
    /// into any particular state by a child that can never be ticked.
    /// </summary>
    internal void RecomputeFromChildren()
    {
        var selectable = _children.Where(c => !c.IsDenylisted).ToList();
        if (selectable.Count > 0)
        {
            if (selectable.All(c => c.CheckState == NodeCheckState.Checked) && HiddenChildCount == 0)
                CheckState = NodeCheckState.Checked;
            else if (selectable.All(c => c.CheckState == NodeCheckState.Unchecked))
                CheckState = NodeCheckState.Unchecked;
            else
                CheckState = NodeCheckState.Indeterminate;
        }
        Parent?.RecomputeFromChildren();
    }
}

/// <summary>
/// Owns one tree rooted at a backup destination's SourceRoot, the current
/// selection, and the mapping between that selection and Include glob
/// patterns - the actual behaviour under test; BackupPickerDialog is a thin
/// WinForms shell around this.
///
/// THE central correctness risk (see the design spec): a pattern the tree
/// cannot represent must never be lost by opening the dialog. <see
/// cref="LoadFromPatterns"/> recognizes exactly two shapes - "relPath/**"
/// for a real directory and a literal relPath for a real file - and treats
/// everything else (a hand-written "**/*.md", a pattern whose target does
/// not exist, or a pattern that would point at a denylisted file) as
/// UNREPRESENTABLE: preserved verbatim in <see cref="UnrepresentablePatterns"/>
/// and re-emitted unchanged by <see cref="GeneratePatterns"/>, never dropped.
/// </summary>
public sealed class BackupTreeModel
{
    /// <summary>Matches the design spec's "~500 children" display cap.</summary>
    public const int DefaultChildCap = 500;

    private readonly HashSet<string> _checkedDirectories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checkedFiles = new(StringComparer.Ordinal);

    public string SourceRoot { get; }
    public FileTreeNode Root { get; }

    /// <summary>
    /// Include patterns that LoadFromPatterns could not map onto a tree
    /// selection - preserved untouched and always included by
    /// <see cref="GeneratePatterns"/>. Empty for a model built via the plain
    /// constructor (nothing loaded yet).
    /// </summary>
    public IReadOnlyList<string> UnrepresentablePatterns { get; private set; } = Array.Empty<string>();

    public BackupTreeModel(string sourceRoot)
    {
        SourceRoot = sourceRoot;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
        if (string.IsNullOrEmpty(name))
            name = sourceRoot; // defensive - a root path with no final segment (e.g. "C:\") still needs a display name
        Root = new FileTreeNode(name, relativePath: "", fullPath: sourceRoot, isDirectory: true, parent: null);
    }

    /// <summary>
    /// Builds a model from an existing Include list: best-effort re-selects
    /// whatever patterns are representable, and preserves the rest. See the
    /// class doc comment - this is the single most important behaviour in
    /// the picker to get right.
    /// </summary>
    public static BackupTreeModel LoadFromPatterns(string sourceRoot, IReadOnlyList<string> include)
    {
        var model = new BackupTreeModel(sourceRoot);
        var unrepresentable = new List<string>();

        foreach (var pattern in include)
        {
            if (TryRecognizePattern(sourceRoot, pattern, out var relativePath, out var isDirectorySelect))
            {
                if (isDirectorySelect)
                    model._checkedDirectories.Add(relativePath);
                else
                    model._checkedFiles.Add(relativePath);
            }
            else
            {
                unrepresentable.Add(pattern);
            }
        }

        model.UnrepresentablePatterns = unrepresentable;
        return model;
    }

    /// <summary>
    /// Recognizes exactly two pattern shapes against the real filesystem
    /// under sourceRoot - see the class doc comment. A pattern whose target
    /// is denylisted is deliberately NOT recognized (falls through to
    /// "unrepresentable" instead) even though its shape would otherwise
    /// qualify: ticking a node is how the tree selects something, denylisted
    /// nodes can never be ticked, and silently pre-ticking one anyway would
    /// both misrepresent the tree's own rule and risk being un-ticked (and
    /// so lost) the moment the user touches anything nearby. Preserving it
    /// verbatim instead keeps the pattern in the config, informs the user it
    /// is not representable, and never removes it just because the dialog
    /// was opened.
    /// </summary>
    private static bool TryRecognizePattern(string sourceRoot, string pattern, out string relativePath, out bool isDirectorySelect)
    {
        relativePath = "";
        isDirectorySelect = false;

        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var normalized = pattern.Replace('\\', '/').Trim();

        if (normalized.EndsWith("/**", StringComparison.Ordinal))
        {
            var dirRel = normalized[..^3];
            if (dirRel.Length == 0 || dirRel.Contains('*') || dirRel.Contains('?'))
                return false;
            if (SecretDenylist.IsSecret(dirRel))
                return false;
            var full = Path.Combine(sourceRoot, dirRel.Replace('/', Path.DirectorySeparatorChar));
            if (!SafeDirectoryExists(full))
                return false;

            relativePath = dirRel;
            isDirectorySelect = true;
            return true;
        }

        if (normalized.Contains('*') || normalized.Contains('?'))
            return false;
        if (SecretDenylist.IsSecret(normalized))
            return false;

        var fileFull = Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!SafeFileExists(fileFull))
            return false;

        relativePath = normalized;
        isDirectorySelect = false;
        return true;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Populates <paramref name="node"/>'s immediate children (one level,
    /// never recursive) if not already loaded. Cheap and synchronous - it is
    /// a single directory listing, not a walk - so it is safe to call
    /// directly on the UI thread when a node is expanded; the design spec's
    /// "off the UI thread" requirement is about SIZE computation, which this
    /// method deliberately does NOT do for directories (see below), not
    /// about listing entries.
    ///
    /// Directories sort before files, each group alphabetical
    /// (case-insensitive), and the combined list is capped at
    /// <paramref name="cap"/> entries - <see cref="FileTreeNode.HiddenChildCount"/>
    /// reports what was left out. A file child's size is set immediately
    /// (a single FileInfo.Length stat is cheap); a directory child's size is
    /// left null - the caller (BackupPickerDialog) is responsible for
    /// computing it via ClaudeLocationScanner.ComputeStats on a background
    /// thread and calling SetSize once it is ready, so that opening a
    /// directory with thousands of files (skills/ was 4,941 on the reference
    /// machine) never blocks this call or the UI thread.
    ///
    /// Never throws: an unreadable directory is treated as having zero
    /// children, exactly like FileSelector's own directory walk.
    /// </summary>
    public void EnsureChildrenLoaded(FileTreeNode node, int cap = DefaultChildCap)
    {
        if (node.ChildrenLoaded || !node.IsDirectory)
            return;
        node.ChildrenLoaded = true;

        string[] subDirs;
        string[] files;
        try
        {
            subDirs = Directory.GetDirectories(node.FullPath);
            files = Directory.GetFiles(node.FullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            node.TotalChildCount = 0;
            return;
        }

        Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        node.TotalChildCount = subDirs.Length + files.Length;

        var created = 0;
        foreach (var dir in subDirs)
        {
            if (created >= cap) break;
            AddChild(node, Path.GetFileName(dir), dir, isDirectory: true);
            created++;
        }
        foreach (var file in files)
        {
            if (created >= cap) break;
            AddChild(node, Path.GetFileName(file), file, isDirectory: false);
            created++;
        }
    }

    private void AddChild(FileTreeNode parent, string name, string fullPath, bool isDirectory)
    {
        var relativePath = parent.RelativePath.Length == 0 ? name : parent.RelativePath + "/" + name;
        var child = new FileTreeNode(name, relativePath, fullPath, isDirectory, parent);

        if (!isDirectory)
        {
            // Cheap single stat - unlike a directory's recursive size, this
            // does not need to be deferred off-thread.
            try { child.SizeBytes = new FileInfo(fullPath).Length; child.FileCount = 1; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* leave unknown */ }
        }

        if (!child.IsDenylisted)
        {
            if (parent.CheckState == NodeCheckState.Checked)
            {
                // The whole parent directory was already selected (directly
                // ticked, or itself inherited from ITS parent) before this
                // child ever existed - a newly-discovered child of an
                // already-fully-selected directory is part of that
                // selection too.
                child.CheckState = NodeCheckState.Checked;
            }
            else if (IsExactSelection(child.RelativePath, child.IsDirectory))
            {
                child.CheckState = NodeCheckState.Checked;
            }
            else if (HasDescendantSelection(child.RelativePath))
            {
                // Something deeper under this not-yet-expanded child is
                // selected (from a loaded config) - show it as partially
                // selected right away rather than waiting for the user to
                // expand down to find out.
                child.CheckState = NodeCheckState.Indeterminate;
            }
        }

        parent.AddChild(child);
    }

    private bool IsExactSelection(string relativePath, bool isDirectory) =>
        isDirectory ? _checkedDirectories.Contains(relativePath) : _checkedFiles.Contains(relativePath);

    private bool HasDescendantSelection(string relativePath)
    {
        var prefix = relativePath + "/";
        foreach (var d in _checkedDirectories)
            if (d.StartsWith(prefix, StringComparison.Ordinal)) return true;
        foreach (var f in _checkedFiles)
            if (f.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Ticks or unticks <paramref name="node"/>: cascades the new state onto
    /// its loaded descendants, bubbles the resulting state up through its
    /// ancestors, and - critically - forgets any config-derived selection
    /// recorded for this node or anything under it, so a later lazy-load
    /// into this subtree reflects the user's explicit action here rather
    /// than replaying stale defaults from the file that was loaded. Returns
    /// false without changing anything for a denylisted node - the picker
    /// must not offer any route around the denylist.
    /// </summary>
    public bool SetChecked(FileTreeNode node, bool value)
    {
        if (node.IsDenylisted)
            return false;

        ForgetRecognizedSelectionUnder(node.RelativePath);
        node.ApplyCheckedRecursively(value ? NodeCheckState.Checked : NodeCheckState.Unchecked);
        node.Parent?.RecomputeFromChildren();
        return true;
    }

    private void ForgetRecognizedSelectionUnder(string relativePath)
    {
        var prefix = relativePath + "/";
        _checkedDirectories.RemoveWhere(d => d == relativePath || d.StartsWith(prefix, StringComparison.Ordinal));
        _checkedFiles.RemoveWhere(f => f == relativePath || f.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Produces the Include pattern list for the current selection: a
    /// Checked directory becomes "relPath/**", a Checked file becomes its
    /// literal relPath, and every <see cref="UnrepresentablePatterns"/>
    /// entry is carried through unchanged. A representable pattern from the
    /// loaded config that was never materialized into a node this session
    /// (the user never expanded that far) is still included, unchanged,
    /// exactly as loaded - the tree only overrides a pattern's fate once the
    /// user actually interacts with (or expands down to) that part of the
    /// tree; see <see cref="SetChecked"/> and <see cref="AddChild"/>.
    /// </summary>
    public IReadOnlyList<string> GeneratePatterns()
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in Root.Children)
            Collect(child, result, visited);

        foreach (var dir in _checkedDirectories)
            if (!visited.Contains(dir)) result.Add(dir + "/**");
        foreach (var file in _checkedFiles)
            if (!visited.Contains(file)) result.Add(file);

        result.AddRange(UnrepresentablePatterns);

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private void Collect(FileTreeNode node, List<string> result, HashSet<string> visited)
    {
        visited.Add(node.RelativePath);

        if (node.CheckState == NodeCheckState.Checked)
        {
            result.Add(node.IsDirectory ? node.RelativePath + "/**" : node.RelativePath);
            // Everything under an explicitly-Checked directory is already
            // covered by the "/**" pattern just added - mark any
            // config-derived entry still under it visited too, so the
            // fallback loop above does not also re-add it as a redundant
            // duplicate the next time GeneratePatterns runs.
            var prefix = node.RelativePath + "/";
            foreach (var d in _checkedDirectories)
                if (d.StartsWith(prefix, StringComparison.Ordinal)) visited.Add(d);
            foreach (var f in _checkedFiles)
                if (f.StartsWith(prefix, StringComparison.Ordinal)) visited.Add(f);
            return;
        }

        foreach (var child in node.Children)
            Collect(child, result, visited);
    }
}

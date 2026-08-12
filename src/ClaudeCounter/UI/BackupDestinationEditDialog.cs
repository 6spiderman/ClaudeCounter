using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// S17c: add-or-edit fields for exactly ONE <see cref="BackupDestination"/> -
/// used both for "Add..." (a fresh destination whose Id/Kind/SyncProvider
/// were already fixed by <see cref="BackupDestinationKindDialog"/>) and
/// "Edit..." (an existing one) in <see cref="BackupDestinationsDialog"/>.
///
/// Unlike the old Backup tab's GitHub/Drive blocks (S5-S16), THIS dialog
/// never has to swap which kind's fields are showing at runtime - Kind is
/// fixed for the lifetime of one dialog instance (shown as a read-only
/// label), so there is exactly one set of kind-specific controls built once,
/// never toggled. That is what makes this simpler than the code it replaces,
/// not just smaller.
///
/// Mirrors BackupAdvancedDialog's own idiom deliberately: the caller reads
/// plain result PROPERTIES after ShowDialog() returns DialogResult.OK and
/// builds a fresh BackupDestination from them plus the Id/Kind/SyncProvider
/// it already knew - this dialog never hands back a live, mutate-in-place
/// object. That is the exact trap the S17a/S17b design notes flagged for the
/// old BackupConfig.Github/Drive shim (a caller mutating a cached reference
/// only genuinely persists if something remembers to sync it back before
/// save) - this dialog's own API cannot repeat it, because there is no
/// cached reference to mutate in the first place.
///
/// Never construct this (or any Form) from a test except via
/// SettingsFormSmokeTests' dedicated STA helper.
/// </summary>
public sealed class BackupDestinationEditDialog : Form
{
    private const int DialogWidth = 520;
    private const int Pad = 16;
    private const int BottomBarHeight = 52;
    private const int RowGap = 8;
    private const int FieldX = 150;
    private const int InfoButtonSize = 16;

    private readonly Palette _palette;
    private readonly DestinationKind _kind;
    private readonly SyncProvider _syncProvider;
    private readonly string _sourceRoot;
    private ToolTip? _helpTip;

    private TextBox _nameBox = null!;
    private ThemedCheckBox _enabledCheck = null!;

    private TextBox? _remoteUrlBox;
    private TextBox? _branchBox;
    private TextBox? _folderPathBox;
    private TextBox? _rcloneRemoteBox;

    private TextBox _includeBox = null!;
    private TextBox _excludeBox = null!;

    private ThemedCheckBox? _keepLastEnabled;
    private NumericUpDown? _keepLastCount;
    private ThemedCheckBox? _deleteOlderEnabled;
    private NumericUpDown? _deleteOlderDays;

    // Named DestinationName/DestinationEnabled, not Name/Enabled - Control
    // already declares both of those, and a same-named property here would
    // silently HIDE the base member (CS0108) rather than genuinely
    // overriding it, which is exactly the "looks right, quietly is not" trap
    // this class's own doc comment warns against repeating.
    public string DestinationName => _nameBox.Text.Trim();
    public bool DestinationEnabled => _enabledCheck.Checked;
    public string RemoteUrl => _remoteUrlBox?.Text.Trim() ?? "";
    public string Branch => _branchBox?.Text.Trim() ?? "main";
    public string FolderPath => _folderPathBox?.Text.Trim() ?? "";
    public string RcloneRemote => _rcloneRemoteBox?.Text.Trim() ?? "";
    public List<string> Include => SplitLines(_includeBox.Text);
    public List<string> Exclude => SplitLines(_excludeBox.Text);

    /// <summary>Null when the "Keep only the most recent" checkbox is unticked, or this destination's Kind is GitHub (no such control exists) - the rule is off.</summary>
    public int? KeepLastCount => _keepLastEnabled is { Checked: true } ? (int)_keepLastCount!.Value : null;

    /// <summary>Null when the "Delete backups older than" checkbox is unticked, or this destination's Kind is GitHub - the rule is off.</summary>
    public int? DeleteOlderThanDays => _deleteOlderEnabled is { Checked: true } ? (int)_deleteOlderDays!.Value : null;

    /// <summary>
    /// <paramref name="seed"/> supplies every field's STARTING value (blank/
    /// disabled defaults for Add, the existing destination's own values for
    /// Edit) - never mutated by this dialog; every field is read back out
    /// through this dialog's own properties above once ShowDialog() returns
    /// DialogResult.OK. <paramref name="isNew"/> gates the one piece of
    /// "helpful magic" this dialog does on its own: auto-filling a blank
    /// sync-folder path when there is exactly one Detect... candidate, which
    /// must never fire for an Edit of an existing, already-configured
    /// destination.
    /// </summary>
    public BackupDestinationEditDialog(
        Palette palette, DestinationKind kind, SyncProvider syncProvider, string sourceRoot,
        BackupDestination seed, bool isNew)
    {
        _palette = palette;
        _kind = kind;
        _syncProvider = syncProvider;
        _sourceRoot = sourceRoot;

        Text = isNew ? "Add Backup Destination" : "Edit Backup Destination";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = palette.Back;
        ForeColor = palette.Fore;
        KeyPreview = true;

        _helpTip = new ToolTip { BackColor = palette.BarBack, ForeColor = palette.Fore, ShowAlways = true };

        var fullWidth = DialogWidth - Pad * 2;
        var rightEdgeX = Pad + fullWidth - InfoButtonSize;
        var y = Pad;

        var kindLabel = NewSectionLabel($"Kind: {BackupDestinationNaming.KindDisplayName(kind, syncProvider)}", palette, y, bold: true);
        Controls.Add(kindLabel);
        y += kindLabel.PreferredHeight + RowGap;

        Controls.Add(NewFieldLabel("Name", palette, y));
        _nameBox = NewTextBox(seed.Name, palette, fullWidth - FieldX);
        _nameBox.Location = new Point(FieldX, y);
        Controls.Add(_nameBox);
        AddInfoButton(rightEdgeX, y - 3, BackupHelpText.Name);
        y += _nameBox.Height + RowGap;

        var enabledTopic = kind == DestinationKind.GitHub ? BackupHelpText.GithubEnabled : BackupHelpText.DestinationEnabled;
        var enabledLabelText = kind == DestinationKind.GitHub ? "Back up to this GitHub repo" : "Back up to this destination";
        _enabledCheck = NewCheckBox(enabledLabelText, seed.Enabled, palette);
        _enabledCheck.Location = new Point(Pad, y);
        Controls.Add(_enabledCheck);
        AddInfoButton(rightEdgeX, y, enabledTopic);
        y += _enabledCheck.Height + RowGap;

        y = kind switch
        {
            DestinationKind.GitHub => BuildGitHubFields(seed, fullWidth, rightEdgeX, y),
            DestinationKind.SyncFolder => BuildSyncFolderFields(seed, isNew, fullWidth, rightEdgeX, y),
            _ => BuildRcloneFields(seed, fullWidth, rightEdgeX, y),
        };

        var includeLabel = NewSectionLabel("Include (one pattern per line)", palette, y);
        Controls.Add(includeLabel);
        AddInfoButton(rightEdgeX, y, BackupHelpText.Include);
        var chooseButton = NewFlatButton("Choose files...", palette);
        chooseButton.Location = new Point(rightEdgeX - InfoButtonSize - 8 - chooseButton.Width, y - 3);
        chooseButton.Click += (_, _) =>
        {
            var current = SplitLines(_includeBox.Text);
            using var picker = new BackupPickerDialog(palette, DestinationName.Length > 0 ? DestinationName : "this destination", sourceRoot, current);
            if (picker.ShowDialog(this) == DialogResult.OK)
                _includeBox.Text = string.Join(Environment.NewLine, picker.Include);
        };
        Controls.Add(chooseButton);
        y += Math.Max(includeLabel.PreferredHeight, chooseButton.Height) + 2;

        _includeBox = NewTextBox(string.Join(Environment.NewLine, seed.Include), palette, fullWidth, multiline: true, height: 55);
        _includeBox.Location = new Point(Pad, y);
        Controls.Add(_includeBox);
        y += _includeBox.Height + RowGap;

        var excludeLabel = NewSectionLabel("Exclude (one pattern per line)", palette, y);
        Controls.Add(excludeLabel);
        AddInfoButton(rightEdgeX, y, BackupHelpText.Exclude);
        y += excludeLabel.PreferredHeight + 2;
        _excludeBox = NewTextBox(string.Join(Environment.NewLine, seed.Exclude), palette, fullWidth, multiline: true, height: 55);
        _excludeBox.Location = new Point(Pad, y);
        Controls.Add(_excludeBox);
        y += _excludeBox.Height + RowGap;

        y = BuildRetentionSection(kind, seed, fullWidth, rightEdgeX, y);

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = palette.BarBack };
        bottomBar.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(DialogWidth, 1), BackColor = palette.Border });

        var cancelButton = NewDialogButton("Cancel", palette);
        cancelButton.DialogResult = DialogResult.Cancel;

        var okButton = NewDialogButton("OK", palette);
        okButton.Click += OnOk;

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

        ClientSize = new Size(DialogWidth, y + Pad + BottomBarHeight);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private int BuildGitHubFields(BackupDestination seed, int fullWidth, int rightEdgeX, int y)
    {
        var urlLabel = NewSectionLabel("Remote URL", _palette, y);
        Controls.Add(urlLabel);
        AddInfoButton(rightEdgeX, y, BackupHelpText.RemoteUrl);
        y += urlLabel.PreferredHeight + 2;

        _remoteUrlBox = NewTextBox(seed.RemoteUrl, _palette, fullWidth);
        _remoteUrlBox.Location = new Point(Pad, y);
        Controls.Add(_remoteUrlBox);
        y += _remoteUrlBox.Height + RowGap;

        // I2: the security model's non-negotiable guardrail - ClaudeCounter
        // has no way to call the GitHub API and check a repo's visibility, so
        // it cannot enforce privacy. The one thing it can do is make sure the
        // user is not left assuming it was checked for them.
        var privacyCaption = NewSubtleLabel(
            "This repo must be private. ClaudeCounter cannot verify that automatically.",
            _palette, fullWidth);
        privacyCaption.Location = new Point(Pad, y);
        Controls.Add(privacyCaption);
        y += privacyCaption.PreferredHeight + RowGap;

        Controls.Add(NewFieldLabel("Branch", _palette, y));
        _branchBox = NewTextBox(string.IsNullOrEmpty(seed.Branch) ? "main" : seed.Branch, _palette, 150);
        _branchBox.Location = new Point(FieldX, y);
        Controls.Add(_branchBox);
        AddInfoButton(FieldX + 150 + 8, y + 3, BackupHelpText.Branch);
        y += _branchBox.Height + RowGap;

        return y;
    }

    private int BuildSyncFolderFields(BackupDestination seed, bool isNew, int fullWidth, int rightEdgeX, int y)
    {
        var label = NewSectionLabel("Sync folder path", _palette, y);
        Controls.Add(label);
        AddInfoButton(rightEdgeX, y, BackupHelpText.SyncFolder);

        var detectButton = NewFlatButton("Detect...", _palette);
        var browseButton = NewFlatButton("Browse...", _palette);
        detectButton.Location = new Point(rightEdgeX - InfoButtonSize - 8 - detectButton.Width, y - 3);
        browseButton.Location = new Point(detectButton.Left - 8 - browseButton.Width, y - 3);
        detectButton.Click += (_, _) => OnDetect();
        browseButton.Click += (_, _) => OnBrowse();
        Controls.Add(detectButton);
        Controls.Add(browseButton);
        y += Math.Max(label.PreferredHeight, Math.Max(detectButton.Height, browseButton.Height)) + 2;

        // S17c: the one-time auto-fill convenience the old top-level "Back up
        // to" selector used to do the instant a sync-folder option was
        // picked - only for a brand new, still-blank destination, and only
        // when there is exactly ONE candidate (a multi-candidate picker
        // popping up from inside this dialog's own constructor, before it is
        // even shown, would be an odd nested-dialog experience; Detect...
        // above always covers that case afterward).
        var initialPath = seed.FolderPath;
        if (isNew && string.IsNullOrEmpty(initialPath))
        {
            var candidates = SyncFolderScanner.FilterByProvider(SyncFolderScanner.Detect(), _syncProvider);
            if (candidates.Count == 1)
                initialPath = candidates[0].Path;
        }

        _folderPathBox = NewTextBox(initialPath, _palette, fullWidth);
        _folderPathBox.Location = new Point(Pad, y);
        Controls.Add(_folderPathBox);
        y += _folderPathBox.Height + RowGap;

        return y;
    }

    private int BuildRcloneFields(BackupDestination seed, int fullWidth, int rightEdgeX, int y)
    {
        Controls.Add(NewFieldLabel("Rclone remote", _palette, y));
        _rcloneRemoteBox = NewTextBox(seed.RcloneRemote, _palette, 200);
        _rcloneRemoteBox.Location = new Point(FieldX, y);
        Controls.Add(_rcloneRemoteBox);
        AddInfoButton(FieldX + 200 + 8, y + 3, BackupHelpText.RcloneRemote);
        y += _rcloneRemoteBox.Height + RowGap;

        return y;
    }

    /// <summary>
    /// GitHub keeps full history via commits - there is nothing for
    /// retention to prune, so this section becomes an honest explanatory
    /// label instead of a dead (always-blank, never-read) pair of controls.
    /// See the task brief's explicit "surface that difference honestly
    /// rather than showing a dead control".
    /// </summary>
    private int BuildRetentionSection(DestinationKind kind, BackupDestination seed, int fullWidth, int rightEdgeX, int y)
    {
        var header = NewSectionLabel("Retention", _palette, y, bold: true);
        Controls.Add(header);
        AddInfoButton(rightEdgeX, y, BackupHelpText.Retention);
        y += header.PreferredHeight + 2;

        if (kind == DestinationKind.GitHub)
        {
            var note = NewSubtleLabel(
                "GitHub keeps full history via commits - retention settings do not apply here.",
                _palette, fullWidth);
            note.Location = new Point(Pad, y);
            Controls.Add(note);
            y += note.PreferredHeight + RowGap;
            return y;
        }

        var unionNote = NewSubtleLabel(
            "When both are ticked, a backup is pruned if EITHER rule would remove it. " +
            "The single most recent backup is never deleted, whatever these settings say.",
            _palette, fullWidth);
        unionNote.Location = new Point(Pad, y);
        Controls.Add(unionNote);
        y += unionNote.PreferredHeight + RowGap;

        _keepLastEnabled = NewCheckBox("Keep only the most recent", seed.KeepLastCount.HasValue, _palette);
        _keepLastEnabled.Location = new Point(Pad, y);
        Controls.Add(_keepLastEnabled);

        _keepLastCount = NewNumeric(_palette, 1, 3650, seed.KeepLastCount ?? 30);
        _keepLastCount.Location = new Point(_keepLastEnabled.Right + 6, y - 2);
        Controls.Add(_keepLastCount);

        var keepLastLabel = NewSectionLabel("backup(s)", _palette, y);
        keepLastLabel.Location = new Point(_keepLastCount.Right + 6, y + 4);
        Controls.Add(keepLastLabel);
        y += _keepLastEnabled.Height + RowGap;

        _keepLastEnabled.CheckedChanged += (_, _) => _keepLastCount.Enabled = _keepLastEnabled.Checked;
        _keepLastCount.Enabled = _keepLastEnabled.Checked;

        _deleteOlderEnabled = NewCheckBox("Delete backups older than", seed.DeleteOlderThanDays.HasValue, _palette);
        _deleteOlderEnabled.Location = new Point(Pad, y);
        Controls.Add(_deleteOlderEnabled);

        _deleteOlderDays = NewNumeric(_palette, 1, 3650, seed.DeleteOlderThanDays ?? 90);
        _deleteOlderDays.Location = new Point(_deleteOlderEnabled.Right + 6, y - 2);
        Controls.Add(_deleteOlderDays);

        var deleteOlderLabel = NewSectionLabel("day(s)", _palette, y);
        deleteOlderLabel.Location = new Point(_deleteOlderDays.Right + 6, y + 4);
        Controls.Add(deleteOlderLabel);
        y += _deleteOlderEnabled.Height + RowGap;

        _deleteOlderEnabled.CheckedChanged += (_, _) => _deleteOlderDays.Enabled = _deleteOlderEnabled.Checked;
        _deleteOlderDays.Enabled = _deleteOlderEnabled.Checked;

        return y;
    }

    private void OnBrowse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder your sync client (Google Drive, OneDrive, Dropbox) or NAS already watches.",
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(_folderPathBox!.Text.Trim()))
            dialog.SelectedPath = _folderPathBox.Text.Trim();

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _folderPathBox.Text = dialog.SelectedPath;
    }

    private void OnDetect()
    {
        var candidates = SyncFolderScanner.FilterByProvider(SyncFolderScanner.Detect(), _syncProvider);
        using var dialog = new SyncFolderDetectDialog(_palette, candidates);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedPath is { } path)
            _folderPathBox!.Text = path;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (DestinationName.Length == 0)
        {
            MessageBox.Show(this, "Name cannot be empty.", "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_kind == DestinationKind.GitHub && SettingsForm.HasEmbeddedCredential(RemoteUrl))
        {
            MessageBox.Show(this,
                "Remote URL must not embed a credential (e.g. https://user:token@host/...). " +
                "backup.json is never allowed to contain a secret - set up Git Credential " +
                "Manager (or an SSH key) for this remote instead.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_kind == DestinationKind.Rclone && SettingsForm.HasLeadingDash(RcloneRemote))
        {
            MessageBox.Show(this,
                "Rclone remote must not start with '-' - rclone would parse it as an option " +
                "rather than a remote name.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_kind == DestinationKind.SyncFolder && DestinationEnabled)
        {
            var folderError = SyncFolderPathValidator.Validate(FolderPath, _sourceRoot);
            if (folderError is not null)
            {
                MessageBox.Show(this, folderError, "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddInfoButton(int x, int y, string text)
    {
        var button = new InfoButton(_palette) { Location = new Point(x, y) };
        button.Click += (_, _) => _helpTip!.Show(text, button, button.Width + 4, 0, 15000);
        Controls.Add(button);
    }

    private static List<string> SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _helpTip?.Dispose();
        base.Dispose(disposing);
    }

    private static Label NewFieldLabel(string text, Palette palette, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9f),
        ForeColor = palette.Fore,
        BackColor = Color.Transparent,
        Location = new Point(Pad, y + 4),
    };

    private static Label NewSectionLabel(string text, Palette palette, int y, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        Font = bold ? new Font("Segoe UI Semibold", 9f) : new Font("Segoe UI", 9f),
        ForeColor = palette.Fore,
        BackColor = Color.Transparent,
        Location = new Point(Pad, y),
    };

    private static Label NewSubtleLabel(string text, Palette palette, int maxWidth) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9f),
        MaximumSize = new Size(maxWidth, 0),
        ForeColor = palette.SubtleFore,
        BackColor = Color.Transparent,
    };

    private static ThemedCheckBox NewCheckBox(string text, bool @checked, Palette palette) => new(palette)
    {
        Text = text,
        AutoSize = true,
        Checked = @checked,
        Font = new Font("Segoe UI", 9f),
    };

    private static TextBox NewTextBox(string text, Palette palette, int width, bool multiline = false, int height = 23)
    {
        var box = new TextBox
        {
            Text = text,
            Width = width,
            Multiline = multiline,
            Font = new Font("Segoe UI", 9f),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
        };
        if (multiline)
        {
            box.Height = height;
            box.ScrollBars = ScrollBars.Vertical;
        }
        return box;
    }

    private static NumericUpDown NewNumeric(Palette palette, int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Width = 60,
        Font = new Font("Segoe UI", 9f),
        BackColor = palette.Back,
        ForeColor = palette.Fore,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static Button NewFlatButton(string text, Palette palette)
    {
        var button = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat,
            BackColor = palette.BarBack,
            ForeColor = palette.Fore,
            Padding = new Padding(10, 4, 10, 4),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        button.FlatAppearance.BorderColor = palette.Border;
        button.Size = button.GetPreferredSize(Size.Empty);
        return button;
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
    /// Duplicates SettingsForm's private InfoButton control (same painting,
    /// same click-shows-tooltip behaviour) - kept as a private nested type
    /// here too rather than shared, mirroring how SettingsForm's own
    /// InfoButton is private to that file; extracting a shared public type
    /// for one small Control was judged not worth the churn for this task.
    /// </summary>
    private sealed class InfoButton : Control
    {
        private static readonly Font InfoFont = new("Segoe UI", 7.5f, FontStyle.Bold);
        private readonly Palette _palette;

        public InfoButton(Palette palette)
        {
            _palette = palette;
            Size = new Size(InfoButtonSize, InfoButtonSize);
            Font = InfoFont;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? _palette.Back);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var circle = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var pen = new Pen(_palette.SubtleFore))
                e.Graphics.DrawEllipse(pen, circle);

            TextRenderer.DrawText(e.Graphics, "i", Font, ClientRectangle, _palette.SubtleFore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        }
    }
}

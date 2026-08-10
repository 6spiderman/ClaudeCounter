using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// The Backup tab's "Restore..." dialog - the UI half of restore (design spec
/// step 3 onward; listing/materialisation/classification/apply are all
/// already built and reviewed in src/ClaudeCounter.Shared/Restore). This
/// class only sequences the flow and paints it; every actual safety
/// decision - what counts as New/Changed/Identical/LiveOnly, what the
/// denylist and containment checks refuse, where the safety copy goes - is
/// made by the engine, not here. This dialog's whole job is to make those
/// decisions visible and to never offer a control that routes around them.
///
/// Flow (one Form, three panels swapped via Visible - same convention
/// SettingsForm's tab strip and BackupPickerDialog's single-page layout
/// both use):
///   1. Choose source + snapshot (<see cref="BuildChoosePanel"/>) - only
///      destinations enabled in the passed-in BackupConfig are offered.
///      Listing runs synchronously (a `git log`/`rclone lsjson` round trip is
///      short enough that RunBackupNowAsync-style blocking is acceptable
///      here, and it is what lets a test construct this dialog with an
///      already-populated snapshot list deterministically via a fake
///      IProcessRunner - see SettingsFormSmokeTests).
///   2. Materialise the chosen snapshot and classify it against live
///      ~/.claude (<see cref="OnPreviewClicked"/>) - both off the UI thread
///      per the design spec, via Task.Run/await (never launched by anything
///      a smoke test's construct-only pass ever calls).
///   3. Preview (<see cref="BuildPreviewPanel"/>) - every classified file,
///      defaulting to all New/Changed ticked and nothing else selectable
///      (see RestoreDisplayModel - the one place that rule lives, tested
///      independently of this Form).
///   4. Apply (<see cref="OnApplyClicked"/>) - warns if Claude Code appears
///      to be running (proceed/cancel, never a hard block), confirms with
///      counts, then applies off the UI thread and shows the result
///      (<see cref="BuildResultPanel"/>), with the safety-copy path shown
///      prominently - it is the only undo mechanism.
///
/// Never construct this (or any Form) from a test except via
/// SettingsFormSmokeTests' dedicated STA helper.
/// </summary>
public sealed class RestoreDialog : Form
{
    private enum Source { Github, Drive }

    private enum Step { ChooseSnapshot, Preview, Result }

    private const int DialogWidth = 700;
    private const int ContentHeight = 380;
    private const int BottomBarHeight = 52;
    private const int Pad = 16;
    private const int RowGap = 8;
    private const int FieldX = 140;
    private const int ListHeight = 240;

    private readonly Palette _palette;
    private readonly BackupConfig _config;
    private readonly RestoreGitSource _gitSource;
    private readonly RestoreZipSource _zipSource;
    private readonly string _liveRoot;
    private readonly string _gitStagingDir;
    private readonly string _driveTempDir;
    private readonly string _stagedRoot;
    private readonly string _safetyBaseDir;
    private readonly List<(Source Kind, string Name)> _destinations = new();

    private Source _selectedSource;
    private IReadOnlyList<RestoreFileRow> _rows = Array.Empty<RestoreFileRow>();
    private bool _busy;
    private Step _currentStep = Step.ChooseSnapshot;

    // Assigned from the Build*Panel helpers below, not directly in the
    // constructor body - mirrors SettingsForm's own fields (see its own
    // "No longer readonly" comment for why: a readonly field can only be
    // assigned directly within a constructor, not by a method it calls).
    private Panel _choosePanel = null!;
    private Panel _previewPanel = null!;
    private Panel _resultPanel = null!;

    private ComboBox? _sourceCombo;
    private ListView _snapshotList = null!;
    private Label _sourceErrorLabel = null!;

    private ListView _fileList = null!;
    private Label _countsLabel = null!;

    private Label _resultMessageLabel = null!;
    private TextBox _safetyCopyBox = null!;
    private Label _skippedLabel = null!;

    private Button _backButton = null!;
    private Button _cancelButton = null!;
    private Button _previewButton = null!;
    private Button _applyButton = null!;
    private Button _closeButton = null!;
    private Label _statusLabel = null!;
    private ProgressBar _progress = null!;

    /// <summary>
    /// <paramref name="scratchRoot"/> is a test-only seam: null (every real
    /// caller, e.g. SettingsForm.OnOpenRestoreDialog) uses the real
    /// %LOCALAPPDATA%, exactly as every other restore-owned scratch path in
    /// this app does (see Program.cs's own "local" variable). A test that
    /// wants to construct this dialog with an already-populated snapshot
    /// list (see SettingsFormSmokeTests) supplies a fake IProcessRunner AND
    /// a temp directory here, so LoadSnapshots' git-clone-into-staging-dir
    /// side effect (simulated by the fake runner) never touches the real
    /// user's %LOCALAPPDATA%\ClaudeCounter - a bare fake IProcessRunner
    /// alone would not be enough to prevent that, since the directory PATH
    /// itself is what needs to be test-isolated, not just what runs inside it.
    /// </summary>
    public RestoreDialog(Palette palette, BackupConfig config, IProcessRunner runner, string? scratchRoot = null)
    {
        _palette = palette;
        _config = config;
        _liveRoot = config.SourceRoot;

        var local = scratchRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _gitStagingDir = Path.Combine(local, "ClaudeCounter", "restore-git-repo");
        _driveTempDir = Path.Combine(local, "ClaudeCounter", "restore-drive-tmp");
        _stagedRoot = Path.Combine(local, "ClaudeCounter", "restore-staged");
        _safetyBaseDir = Path.Combine(local, "ClaudeCounter", "restore-safety");

        _gitSource = new RestoreGitSource(runner, _gitStagingDir);
        _zipSource = new RestoreZipSource(runner);

        if (config.Github.Enabled) _destinations.Add((Source.Github, "GitHub"));
        if (config.Drive.Enabled) _destinations.Add((Source.Drive, "Google Drive"));

        Text = "Restore from backup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = palette.Back;
        ForeColor = palette.Fore;
        KeyPreview = true;

        var contentHost = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(DialogWidth, ContentHeight),
            BackColor = palette.Back,
        };

        _choosePanel = BuildChoosePanel(palette);
        _previewPanel = BuildPreviewPanel(palette);
        _resultPanel = BuildResultPanel(palette);
        contentHost.Controls.Add(_resultPanel);
        contentHost.Controls.Add(_previewPanel);
        contentHost.Controls.Add(_choosePanel);
        Controls.Add(contentHost);

        var bottomBar = BuildBottomBar(palette);
        Controls.Add(bottomBar);

        ClientSize = new Size(DialogWidth, ContentHeight + BottomBarHeight);

        AcceptButton = null;
        CancelButton = null;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && !_busy)
                Close();
        };
        FormClosing += (_, e) =>
        {
            // Restore is the most dangerous thing in this codebase - the
            // window's own X button (or Alt+F4) must not be able to tear the
            // dialog down mid-materialise/classify/apply, even though the
            // Escape handler above already refuses to act while busy.
            if (_busy) e.Cancel = true;
        };
        FormClosed += (_, _) => CleanupStagingDirectories();

        // Restore rule 1 made visible in the flow itself: nothing above this
        // point can reach ~/.claude - the first step only ever lists and lets
        // the user pick a snapshot.
        if (_destinations.Count == 0)
        {
            SetSourceError("No backup destination is enabled. Enable and configure GitHub or Google Drive backup on the Backup tab first.");
        }
        else
        {
            _selectedSource = _destinations[0].Kind;
            if (_sourceCombo is not null)
                _sourceCombo.SelectedIndex = 0; // fires SelectedIndexChanged -> LoadSnapshots
            else
                LoadSnapshots(_selectedSource);
        }

        ShowStep(Step.ChooseSnapshot);
    }

    // --- Panel 1: choose source + snapshot ---------------------------------

    private Panel BuildChoosePanel(Palette palette)
    {
        var panel = new Panel { Size = new Size(DialogWidth, ContentHeight), BackColor = palette.Back };
        var fullWidth = DialogWidth - Pad * 2;
        var y = Pad;

        if (_destinations.Count > 1)
        {
            panel.Controls.Add(NewSectionLabel("Restore from", palette, y));
            _sourceCombo = NewCombo(palette, 200);
            _sourceCombo.Location = new Point(FieldX, y);
            foreach (var d in _destinations)
                _sourceCombo.Items.Add(d.Name);
            _sourceCombo.SelectedIndexChanged += (_, _) =>
            {
                if (_busy) return;
                _selectedSource = _destinations[_sourceCombo.SelectedIndex].Kind;
                LoadSnapshots(_selectedSource);
            };
            panel.Controls.Add(_sourceCombo);
            y += _sourceCombo.Height + RowGap;
        }
        else
        {
            var text = _destinations.Count == 1
                ? $"Restoring from {_destinations[0].Name}."
                : "No backup destination is enabled.";
            var label = NewSectionLabel(text, palette, y);
            panel.Controls.Add(label);
            y += label.PreferredHeight + RowGap;
        }

        var listLabel = NewSubtleLabel("Available backups, newest first:", palette, fullWidth);
        listLabel.Location = new Point(Pad, y);
        panel.Controls.Add(listLabel);
        y += listLabel.PreferredHeight + 2;

        _snapshotList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Location = new Point(Pad, y),
            Size = new Size(fullWidth, ListHeight),
            Font = Font,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _snapshotList.Columns.Add("When", 150);
        _snapshotList.Columns.Add("Snapshot", fullWidth - 150 - 100);
        _snapshotList.Columns.Add("Size", 100);
        _snapshotList.SelectedIndexChanged += (_, _) =>
            _previewButton.Enabled = !_busy && _snapshotList.SelectedItems.Count > 0;
        panel.Controls.Add(_snapshotList);
        y += _snapshotList.Height + RowGap;

        _sourceErrorLabel = NewSubtleLabel("", palette, fullWidth);
        _sourceErrorLabel.ForeColor = Theme.BandColor(Band.Amber);
        _sourceErrorLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_sourceErrorLabel);

        return panel;
    }

    /// <summary>
    /// Lists snapshots for <paramref name="source"/> and populates
    /// _snapshotList. Synchronous by design - see this class's own doc
    /// comment for why (a short blocking round trip here, unlike materialise/
    /// classify/apply, is what makes "construct with an already-populated
    /// snapshot list" possible for a smoke test with a fake IProcessRunner).
    /// </summary>
    private void LoadSnapshots(Source source)
    {
        _snapshotList.Items.Clear();
        _sourceErrorLabel.Text = "";
        _previewButton.Enabled = false;

        Cursor.Current = Cursors.WaitCursor;
        RestoreListResult result;
        try
        {
            result = source == Source.Github
                ? _gitSource.ListSnapshots(_config.Github)
                : _zipSource.ListSnapshots(_config.Drive);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }

        if (!result.Ok)
        {
            SetSourceError(result.Message);
            return;
        }
        if (result.Snapshots.Count == 0)
        {
            SetSourceError("No backups found yet for this destination.");
            return;
        }

        foreach (var snapshot in result.Snapshots)
        {
            var item = new ListViewItem(new[]
            {
                snapshot.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                snapshot.DisplayName,
                snapshot.SizeBytes is { } size ? FormatSize(size) : "-",
            })
            { Tag = snapshot };
            _snapshotList.Items.Add(item);
        }
    }

    private void SetSourceError(string message)
    {
        _sourceErrorLabel.Text = message;
    }

    // --- Panel 2: preview ----------------------------------------------------

    private Panel BuildPreviewPanel(Palette palette)
    {
        var panel = new Panel { Size = new Size(DialogWidth, ContentHeight), BackColor = palette.Back };
        var fullWidth = DialogWidth - Pad * 2;
        var y = Pad;

        // Restore rule 3, made visible: this is the one sentence in the whole
        // dialog whose entire job is to make "restore never deletes" a thing
        // the user actually reads, not just a property the engine happens to
        // have.
        var banner = NewSubtleLabel(
            "Tick the files to restore. Only New and Changed files can be selected - Identical files need " +
            "no action, and files that exist only in your live configuration (Live only) are never touched: " +
            "restore adds and overwrites, it never deletes.",
            palette, fullWidth);
        banner.Location = new Point(Pad, y);
        panel.Controls.Add(banner);
        y += banner.PreferredHeight + RowGap;

        _fileList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            CheckBoxes = true,
            MultiSelect = false,
            HideSelection = false,
            Location = new Point(Pad, y),
            Size = new Size(fullWidth, ListHeight),
            Font = Font,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _fileList.Columns.Add("Status", 90);
        _fileList.Columns.Add("Path", 280);
        _fileList.Columns.Add("Staged", 140);
        _fileList.Columns.Add("Live", fullWidth - 90 - 280 - 140);
        // Refuses the tick for anything RestoreDisplayModel says is not
        // selectable (Identical/LiveOnly) - the UI-side backstop for restore
        // rule 3, mirroring BackupPickerDialog.OnBeforeCheck's refusal to
        // check a denylisted node. ItemCheck fires BEFORE the state change,
        // so reverting NewValue here is what actually blocks it.
        _fileList.ItemCheck += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= _fileList.Items.Count) return;
            if (_fileList.Items[e.Index].Tag is RestoreFileRow row && !row.Selectable)
                e.NewValue = e.CurrentValue;
        };
        _fileList.ItemChecked += (_, _) => RefreshCounts();
        panel.Controls.Add(_fileList);
        y += _fileList.Height + RowGap;

        _countsLabel = NewSubtleLabel("", palette, fullWidth);
        _countsLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_countsLabel);

        return panel;
    }

    private void PopulatePreview()
    {
        _fileList.Items.Clear();
        foreach (var row in _rows)
        {
            var item = new ListViewItem(new[]
            {
                StatusText(row.Entry.Status),
                row.Entry.RelativePath,
                row.Entry.StagedSizeBytes is { } sb ? FormatSize(sb) : "-",
                row.Entry.LiveSizeBytes is { } lb ? FormatSize(lb) : "-",
            })
            {
                Tag = row,
                Checked = row.DefaultChecked,
                ForeColor = row.Selectable ? _palette.Fore : _palette.SubtleFore,
            };
            _fileList.Items.Add(item);
        }
        RefreshCounts();
    }

    private static string StatusText(RestoreFileStatus status) => status switch
    {
        RestoreFileStatus.New => "New",
        RestoreFileStatus.Changed => "Changed",
        RestoreFileStatus.Identical => "Identical",
        RestoreFileStatus.LiveOnly => "Live only",
        _ => status.ToString(),
    };

    private void RefreshCounts()
    {
        var counts = RestoreDisplayModel.Summarize(_rows);
        var selected = _fileList.Items.Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => ((RestoreFileRow)i.Tag!).Entry.Status)
            .ToList();
        var selectedNew = selected.Count(s => s == RestoreFileStatus.New);
        var selectedChanged = selected.Count(s => s == RestoreFileStatus.Changed);

        _countsLabel.Text =
            $"{selectedNew + selectedChanged} of {counts.New + counts.Changed} new/changed file(s) selected. " +
            $"{counts.Identical} identical (nothing to do). " +
            $"{counts.LiveOnly} present only in your live config - left alone.";

        _applyButton.Enabled = !_busy && (selectedNew + selectedChanged) > 0;
    }

    // --- Panel 3: result -------------------------------------------------

    private Panel BuildResultPanel(Palette palette)
    {
        var panel = new Panel { Size = new Size(DialogWidth, ContentHeight), BackColor = palette.Back };
        var fullWidth = DialogWidth - Pad * 2;
        var y = Pad;

        _resultMessageLabel = NewSubtleLabel("", palette, fullWidth);
        _resultMessageLabel.ForeColor = palette.Fore;
        _resultMessageLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_resultMessageLabel);
        y += 40;

        // Rule 2 made visible, prominently: this path is the only way an
        // unwanted restore gets undone, so it is shown in a selectable,
        // copyable box rather than only ever written to the log.
        var caption = NewSectionLabel("Safety copy of any overwritten files:", palette, y);
        panel.Controls.Add(caption);
        y += caption.PreferredHeight + 2;

        _safetyCopyBox = new TextBox
        {
            ReadOnly = true,
            Location = new Point(Pad, y),
            Width = fullWidth,
            Font = Font,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
        };
        panel.Controls.Add(_safetyCopyBox);
        y += _safetyCopyBox.Height + RowGap;

        _skippedLabel = NewSubtleLabel("", palette, fullWidth);
        _skippedLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_skippedLabel);

        return panel;
    }

    private void PopulateResult(RestoreApplyResult result)
    {
        _resultMessageLabel.Text = result.Ok
            ? result.Message
            : $"Restore did not finish cleanly: {result.Message}";
        _resultMessageLabel.ForeColor = result.Ok ? _palette.Fore : Theme.BandColor(Band.Red);

        _safetyCopyBox.Text = result.SafetyCopyPath ?? "(none needed - no live file was overwritten)";

        _skippedLabel.Text = result.SkippedPaths.Count > 0
            ? $"{result.SkippedPaths.Count} requested file(s) were skipped - see the log for details."
            : "";
    }

    // --- Bottom bar / step management --------------------------------------

    private Panel BuildBottomBar(Palette palette)
    {
        var bar = new Panel { Location = new Point(0, ContentHeight), Size = new Size(DialogWidth, BottomBarHeight), BackColor = palette.BarBack };
        bar.Controls.Add(new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(DialogWidth, 1),
            BackColor = palette.Border,
        });

        _backButton = NewDialogButton("< Back", palette);
        _backButton.Click += (_, _) => { if (!_busy) ShowStep(Step.ChooseSnapshot); };

        _cancelButton = NewDialogButton("Cancel", palette);
        _cancelButton.Click += (_, _) => { if (!_busy) Close(); };

        _previewButton = NewFlatButton("Preview selected snapshot...", palette);
        _previewButton.Enabled = false;
        _previewButton.Click += OnPreviewClicked;

        _applyButton = NewFlatButton("Apply...", palette);
        _applyButton.Enabled = false;
        _applyButton.Click += OnApplyClicked;

        _closeButton = NewDialogButton("Close", palette);
        _closeButton.Click += (_, _) => Close();

        _statusLabel = new Label
        {
            AutoSize = true,
            Font = Font,
            ForeColor = palette.SubtleFore,
            BackColor = Color.Transparent,
            Location = new Point(Pad, 18),
            Visible = false,
        };
        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Size = new Size(120, 16),
            Location = new Point(Pad, 18),
            Visible = false,
        };

        bar.Controls.Add(_backButton);
        bar.Controls.Add(_cancelButton);
        bar.Controls.Add(_previewButton);
        bar.Controls.Add(_applyButton);
        bar.Controls.Add(_closeButton);
        bar.Controls.Add(_statusLabel);
        bar.Controls.Add(_progress);

        return bar;
    }

    private void ShowStep(Step step)
    {
        _currentStep = step;
        _choosePanel.Visible = step == Step.ChooseSnapshot;
        _previewPanel.Visible = step == Step.Preview;
        _resultPanel.Visible = step == Step.Result;

        _backButton.Visible = step == Step.Preview;
        _cancelButton.Visible = step != Step.Result;
        _previewButton.Visible = step == Step.ChooseSnapshot;
        _applyButton.Visible = step == Step.Preview;
        _closeButton.Visible = step == Step.Result;

        LayoutBottomBarButtons();
    }

    private void LayoutBottomBarButtons()
    {
        var cursor = DialogWidth - Pad;
        void PlaceRight(Button b)
        {
            if (!b.Visible) return;
            cursor -= b.Width;
            b.Location = new Point(cursor, (BottomBarHeight - b.Height) / 2);
            cursor -= 8;
        }
        PlaceRight(_closeButton);
        PlaceRight(_applyButton);
        PlaceRight(_previewButton);
        PlaceRight(_cancelButton);
        _backButton.Location = new Point(Pad, (BottomBarHeight - _backButton.Height) / 2);
    }

    /// <summary>
    /// Toggles the busy/progress state shared by every long operation
    /// (materialise+classify, apply). Disables navigation so the window
    /// cannot be driven mid-operation, and re-derives each action button's
    /// Enabled from current selection state afterwards rather than just
    /// flipping it back on - it must stay disabled if, say, nothing is
    /// ticked any more.
    ///
    /// _backButton/_cancelButton are hidden (not just disabled) while busy,
    /// not left in place: both sit at the same Location as _statusLabel/
    /// _progress (the bottom bar's left side), and OnApplyClicked's busy
    /// period happens while the Preview step - where _backButton is normally
    /// shown - is still current, so leaving it visible would draw it right
    /// underneath the "Applying restore..." status text.
    /// </summary>
    private void SetBusy(bool busy, string status = "")
    {
        _busy = busy;
        _statusLabel.Text = status;
        _statusLabel.Visible = busy;
        _progress.Visible = busy;
        _backButton.Visible = !busy && _currentStep == Step.Preview;
        _cancelButton.Visible = !busy && _currentStep != Step.Result;
        _snapshotList.Enabled = !busy;
        _fileList.Enabled = !busy;
        if (_sourceCombo is not null) _sourceCombo.Enabled = !busy;

        _previewButton.Enabled = !busy && _snapshotList.SelectedItems.Count > 0;
        if (!busy) RefreshCounts(); // re-derives _applyButton.Enabled
    }

    // --- Long operations (off the UI thread) --------------------------------

    private async void OnPreviewClicked(object? sender, EventArgs e)
    {
        if (_busy || _snapshotList.SelectedItems.Count == 0)
            return;
        var snapshot = (RestoreSnapshot)_snapshotList.SelectedItems[0].Tag!;
        var source = _selectedSource;

        SetBusy(true, "Materialising snapshot...");
        try
        {
            var materialize = await Task.Run(() => source == Source.Github
                ? _gitSource.Materialize(_config.Github, snapshot.Id, _stagedRoot, _liveRoot)
                : _zipSource.Materialize(_config.Drive, snapshot.Id, _driveTempDir, _stagedRoot, _liveRoot));

            if (IsDisposed) return;
            if (!materialize.Ok)
            {
                MessageBox.Show(this, materialize.Message, "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, "Comparing against your live configuration...");
            var entries = await Task.Run(() => RestoreClassifier.Classify(_stagedRoot, _liveRoot));
            if (IsDisposed) return;

            _rows = RestoreDisplayModel.BuildRows(entries);
            PopulatePreview();
            ShowStep(Step.Preview);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private async void OnApplyClicked(object? sender, EventArgs e)
    {
        if (_busy) return;

        var chosen = _fileList.Items.Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => ((RestoreFileRow)i.Tag!).Entry)
            .ToList();
        if (chosen.Count == 0)
            return;

        // Restore rule 6: warn, never a hard block - the user may know
        // better than a name-based process check.
        if (ClaudeProcessDetector.AppearsRunning())
        {
            var proceedPastWarning = MessageBox.Show(this,
                "Claude Code appears to be running. Restoring now could overwrite files it currently has " +
                "open or is using mid-session, which can corrupt its state.\n\nContinue anyway?",
                "Claude Code may be running", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (proceedPastWarning != DialogResult.Yes)
                return;
        }

        var newCount = chosen.Count(c => c.Status == RestoreFileStatus.New);
        var changedCount = chosen.Count(c => c.Status == RestoreFileStatus.Changed);
        var confirmed = MessageBox.Show(this,
            $"This will write {chosen.Count} file(s) into {_liveRoot}:\n" +
            $"  {newCount} new file(s)\n" +
            $"  {changedCount} existing file(s) overwritten\n\n" +
            "Every file about to be overwritten is copied to a safety folder first, so this can be undone " +
            "by hand. Restore never deletes anything - files that exist only in your live configuration are " +
            "left alone.\n\nContinue?",
            "Confirm restore", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirmed != DialogResult.Yes)
            return;

        SetBusy(true, "Applying restore...");
        try
        {
            var result = await Task.Run(() => RestoreApplier.Apply(_stagedRoot, _liveRoot, chosen, _safetyBaseDir));
            if (IsDisposed) return;

            PopulateResult(result);
            ShowStep(Step.Result);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    /// <summary>
    /// Every restore-owned scratch directory this dialog can have created -
    /// the materialised snapshot, and RestoreGitSource's own clone (a full,
    /// plaintext, multi-version copy of everything ever backed up to
    /// GitHub) - is a second copy of (a subset of) the user's Claude config
    /// sitting on disk once this dialog is done with it. RestoreCleanup
    /// never throws and logs loudly on failure instead, so this is safe to
    /// call unconditionally from FormClosed. The Drive temp dir is included
    /// too even though RestoreZipSource.Materialize already deletes the
    /// downloaded zip itself on every call - best effort, in case a call
    /// failed partway through and left it behind.
    /// </summary>
    private void CleanupStagingDirectories()
    {
        RestoreCleanup.DeleteStagingDirectory(_stagedRoot);
        RestoreCleanup.DeleteStagingDirectory(_gitStagingDir);
        RestoreCleanup.DeleteStagingDirectory(_driveTempDir);
    }

    // --- Small themed control factories (mirrors BackupAdvancedDialog's own) ---

    private static Label NewSectionLabel(string text, Palette palette, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9f),
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

    private static ComboBox NewCombo(Palette palette, int width) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9f),
        Width = width,
        BackColor = palette.Back,
        ForeColor = palette.Fore,
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

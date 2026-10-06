using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// The Backup tab's "Restore..." dialog - the UI half of restore (design spec
/// step 3 onward; listing/materialisation/classification/apply are all
/// already built and reviewed in src/ClaudeCounter.Core/Restore). This
/// class only sequences the flow and paints it; every actual safety
/// decision - what counts as New/Changed/Identical/LiveOnly, what the
/// denylist and containment checks refuse, where the safety copy goes - is
/// made by the engine, not here. This dialog's whole job is to make those
/// decisions visible and to never offer a control that routes around them.
///
/// S10 (restore to a different folder): step 1 also lets the user choose
/// WHERE restore goes - live config (the original, default behaviour) or
/// another folder they browse to. <see cref="RestoreDestinationModel"/>
/// resolves that choice to one root, called exactly once per preview and
/// frozen in _destinationRootForApply for that preview/apply cycle, so
/// classification (step 2) and apply (step 4) are guaranteed to agree on
/// where "the destination" is - the one thing this feature could get wrong
/// that would be worse than not having it at all. The materialisation
/// destination (the staging folder itself) and its protectedRoot guard are
/// untouched by this: they always stage into _stagedRoot and always guard
/// against writing into live config, independent of what the user later
/// chooses to classify/apply against.
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
///   2. Materialise the chosen snapshot and classify it against the chosen
///      destination - live ~/.claude by default, or the folder picked in
///      step 1 (<see cref="OnPreviewClicked"/>) - both off the UI thread per
///      the design spec, via Task.Run/await (never launched by anything a
///      smoke test's construct-only pass ever calls).
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
    private enum Step { ChooseSnapshot, Preview, Result }

    private const int DialogWidth = 700;
    // S10: +48 over the original 380 for the new "Restore into" row (combo +
    // browse button) and its resolved-path label in the choose panel - see
    // BuildChoosePanel. No test enforces this dialog's own height (unlike
    // SettingsForm's Backup tab budget, which this change does not touch at
    // all), so there is no pixel-exact reason to keep it at 380.
    private const int ContentHeight = 428;
    private const int BottomBarHeight = 52;
    private const int Pad = 16;
    private const int RowGap = 8;
    private const int FieldX = 140;
    private const int ListHeight = 240;

    private readonly Palette _palette;
    private readonly BackupConfig _config;
    private readonly IProcessRunner _runner;
    private readonly RestoreZipSource _zipSource;
    private readonly string _liveRoot;

    // S17b: these two are ROOTS, not single scratch directories - a git
    // destination's own clone lives at Path.Combine(_gitStagingRoot,
    // destination.Id), and a zip destination's rclone download temp lives at
    // Path.Combine(_driveTempRoot, destination.Id). See GitSourceFor's own
    // doc comment for why N destinations need this (BackupRunner.
    // RunBackendFor's identical reasoning, applied here). _stagedRoot is NOT
    // split per destination - only one destination is ever previewed/applied
    // at a time in this dialog (SetBusy serializes every long operation), and
    // Materialize always clears its destinationDir before writing, so there
    // is no cross-destination collision to isolate against for that one.
    private readonly string _gitStagingRoot;
    private readonly string _driveTempRoot;
    private readonly string _stagedRoot;
    private readonly string _safetyBaseDir;
    private readonly List<BackupDestination> _destinations = new();

    private BackupDestination? _selectedDestination;
    private IReadOnlyList<RestoreFileRow> _rows = Array.Empty<RestoreFileRow>();
    private bool _busy;
    private Step _currentStep = Step.ChooseSnapshot;

    // S10 (restore to a different folder). _destinationKind/_customDestinationRoot
    // are the live, editable selection (Panel 1 only); _destinationRootForApply
    // is that selection FROZEN the moment a preview's classify succeeds (see
    // OnPreviewClicked) and is what OnApplyClicked actually applies against -
    // never a fresh re-resolution - so classify and apply can never disagree
    // about where "the destination" is, however the destination controls (in
    // the now-hidden Panel 1) might theoretically be poked afterward.
    // _customDestinationRoot persists across switching the combo back to Live
    // and back to Custom again within this dialog's lifetime ("remembered for
    // the session" per the brief) - it is only ever cleared when the user
    // picks a folder that turns out to equal live config (see
    // OnBrowseForDestination) or, trivially, when the dialog is disposed.
    private RestoreDestinationKind _destinationKind = RestoreDestinationKind.Live;
    private string? _customDestinationRoot;
    private string? _destinationRootForApply;

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

    // S10: destination controls, all on Panel 1 (BuildChoosePanel).
    private ComboBox _destinationCombo = null!;
    private Button _destinationBrowseButton = null!;
    private Label _destinationPathLabel = null!;

    private ListView _fileList = null!;
    private Label _countsLabel = null!;
    private Label _previewDestinationLabel = null!;

    private Label _resultMessageLabel = null!;
    private Label _resultDestinationLabel = null!;
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
    ///
    /// The same reasoning that motivates isolating this path in tests is
    /// also why LoadSnapshots and OnPreviewClicked (fix round 1, review)
    /// never let an exception from listing/materialising/classifying escape
    /// uncaught: whatever this constructor points _gitStagingRoot/_stagedRoot
    /// at (real %LOCALAPPDATA% for every real caller) can end up holding a
    /// full, plaintext, multi-version copy of the user's Claude config the
    /// moment `git clone` succeeds - an uncaught exception anywhere after
    /// that point would crash the whole app (Application.ThreadException ->
    /// Environment.Exit) before FormClosed ever runs
    /// CleanupStagingDirectories, leaving that copy on disk indefinitely.
    /// This is a real-caller data-hygiene concern, not just a test-isolation
    /// one.
    /// </summary>
    public RestoreDialog(Palette palette, BackupConfig config, IProcessRunner runner, string? scratchRoot = null)
    {
        _palette = palette;
        _config = config;
        _liveRoot = config.SourceRoot;

        var local = scratchRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _gitStagingRoot = Path.Combine(local, "ClaudeCounter", "restore-git-repo");
        _driveTempRoot = Path.Combine(local, "ClaudeCounter", "restore-drive-tmp");
        _stagedRoot = Path.Combine(local, "ClaudeCounter", "restore-staged");
        _safetyBaseDir = Path.Combine(local, "ClaudeCounter", "restore-safety");

        _runner = runner;
        _zipSource = new RestoreZipSource(runner);

        // S17b: reads BackupConfig.Destinations directly (the real
        // N-destination list) instead of the old Github/Drive shim's fixed
        // pair. Each BackupDestination already carries its own display Name
        // (set at creation - see BackupConfig's migration, which reuses
        // BackupHealth.DriveDisplayName for the "drive" well-known entry, and
        // S16's named-destination UI for anything added since), so there is
        // no separate transport-aware naming step needed here any more.
        _destinations.AddRange(config.Destinations.Where(d => d.Enabled));

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
            // Destination-neutral here (unlike the destination list above,
            // which names each destination by its own configured Name): no
            // destination is enabled at this point, so there is nothing
            // specific to name - S17b generalizes this from "GitHub or
            // Drive" (which assumed exactly two) to a plain "a destination".
            SetSourceError("No backup destination is enabled. Enable and configure a destination on the Backup tab first.");
        }
        else
        {
            _selectedDestination = _destinations[0];
            if (_sourceCombo is not null)
                _sourceCombo.SelectedIndex = 0; // fires SelectedIndexChanged -> LoadSnapshots
            else
                LoadSnapshots(_selectedDestination);
        }

        ShowStep(Step.ChooseSnapshot);
    }

    // --- S17b: per-destination source dispatch ------------------------------

    /// <summary>
    /// A fresh <see cref="RestoreGitSource"/> pointed at THIS destination's
    /// own subdirectory of <see cref="_gitStagingRoot"/> - unlike <see
    /// cref="_zipSource"/> (already stateless: it takes its scratch tempDir
    /// as a per-call parameter, not a constructor field), RestoreGitSource
    /// bakes its clone directory into its constructor, so per-destination
    /// isolation for the GitHub kind means constructing a new instance per
    /// destination rather than reusing one field across all of them - see
    /// this class's own field-block comment on <see cref="_gitStagingRoot"/>
    /// for why that isolation matters with N destinations. Construction
    /// itself does no I/O (it just stores two references), so building one
    /// per call (LoadSnapshots, then again in MaterializeSnapshot) is cheap;
    /// both calls for the SAME destination resolve to the SAME subdirectory,
    /// so the clone LoadSnapshots' EnsureRepo produces is exactly what
    /// MaterializeSnapshot's own EnsureRepo call reuses (fetch, not
    /// re-clone).
    /// </summary>
    private RestoreGitSource GitSourceFor(BackupDestination destination) =>
        new(_runner, Path.Combine(_gitStagingRoot, destination.Id));

    /// <summary>Lists snapshots for <paramref name="destination"/>, dispatching on its Kind without leaking that dispatch to callers.</summary>
    private RestoreListResult ListSnapshotsFor(BackupDestination destination) =>
        destination.Kind == DestinationKind.GitHub
            ? GitSourceFor(destination).ListSnapshots(destination.ToGitTarget())
            : _zipSource.ListSnapshots(destination.ToDriveTarget());

    /// <summary>
    /// Materialises <paramref name="snapshotId"/> for <paramref
    /// name="destination"/> into <paramref name="destinationDir"/>. Wraps
    /// the arity difference between RestoreGitSource.Materialize(target, id,
    /// destDir, protectedRoot) and RestoreZipSource.Materialize(target, id,
    /// tempDir, destDir, protectedRoot) - the caller (OnPreviewClicked)
    /// supplies only what is common to both (the destination, the snapshot,
    /// and where to stage it) and never has to know that Drive-kind
    /// materialisation also needs a download tempDir. That tempDir is this
    /// destination's own subdirectory of <see cref="_driveTempRoot"/> - same
    /// per-destination isolation reasoning as <see cref="GitSourceFor"/>,
    /// applied to the rclone download scratch instead of the git clone.
    /// </summary>
    private RestoreMaterializeResult MaterializeSnapshot(BackupDestination destination, string snapshotId, string destinationDir) =>
        destination.Kind == DestinationKind.GitHub
            ? GitSourceFor(destination).Materialize(destination.ToGitTarget(), snapshotId, destinationDir, _liveRoot)
            : _zipSource.Materialize(destination.ToDriveTarget(), snapshotId, Path.Combine(_driveTempRoot, destination.Id), destinationDir, _liveRoot);

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
                _selectedDestination = _destinations[_sourceCombo.SelectedIndex];
                LoadSnapshots(_selectedDestination);
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

        // S10: "Restore into" - live config (default) or a folder the user
        // browses to. Deliberately on Panel 1, alongside "Restore from": the
        // destination has to be settled before Preview ever runs, since it
        // is what classify (step 2) compares staging against - see this
        // class's own doc comment on why classify and apply must always
        // agree on this value.
        panel.Controls.Add(NewSectionLabel("Restore into", palette, y));
        _destinationCombo = NewCombo(palette, 200);
        _destinationCombo.Location = new Point(FieldX, y);
        _destinationCombo.Items.Add("Live config");
        _destinationCombo.Items.Add("Another folder...");
        _destinationCombo.SelectedIndex = 0;
        _destinationCombo.SelectedIndexChanged += OnDestinationComboChanged;
        panel.Controls.Add(_destinationCombo);

        _destinationBrowseButton = NewFlatButton("Browse...", palette);
        _destinationBrowseButton.Location = new Point(FieldX + _destinationCombo.Width + RowGap, y);
        _destinationBrowseButton.Visible = false;
        _destinationBrowseButton.Click += (_, _) =>
        {
            if (_busy) return;
            OnBrowseForDestination();
            SyncDestinationComboSelection();
            UpdateDestinationLabel();
            RefreshPreviewButtonEnabled();
        };
        panel.Controls.Add(_destinationBrowseButton);
        y += _destinationCombo.Height + RowGap;

        _destinationPathLabel = NewFixedHeightDestinationLabel(palette, fullWidth);
        _destinationPathLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_destinationPathLabel);
        UpdateDestinationLabel();
        y += _destinationPathLabel.Height + RowGap;

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
        _snapshotList.SelectedIndexChanged += (_, _) => RefreshPreviewButtonEnabled();
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
    ///
    /// Fix round 1 (review): this runs synchronously from the constructor -
    /// an exception escaping it would leave a half-constructed Form that
    /// never reaches FormClosed, and therefore never runs
    /// CleanupStagingDirectories, even though RestoreGitSource.ListSnapshots'
    /// own EnsureRepo step can already have cloned a full, plaintext copy of
    /// every backed-up version into this destination's own subdirectory of
    /// _gitStagingRoot by the time a LATER call
    /// (e.g. `git log` itself) throws. Catching here and treating it exactly
    /// like a normal RestoreListResult.Failure keeps the dialog fully
    /// constructed either way, so it still closes and cleans up normally.
    /// </summary>
    private void LoadSnapshots(BackupDestination destination)
    {
        _snapshotList.Items.Clear();
        _sourceErrorLabel.Text = "";
        _previewButton.Enabled = false;

        Cursor.Current = Cursors.WaitCursor;
        RestoreListResult result;
        try
        {
            result = ListSnapshotsFor(destination);
        }
        catch (Exception ex)
        {
            Log.Error($"RestoreDialog: listing snapshots failed unexpectedly: {ex}");
            SetSourceError($"Could not list backups: {ex.Message}");
            return;
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

    // --- S10: destination choice ("Restore into") ---------------------------

    /// <summary>
    /// Every folder this dialog itself writes restore-owned scratch data
    /// into: the materialised staging tree, RestoreGitSource's own clone,
    /// the Drive temp-download directory, and the safety-copy base
    /// directory (RestoreApplier creates a timestamped subfolder under this
    /// one on every apply). A chosen destination that is, contains, or is
    /// contained by any of these would let restore corrupt its own
    /// machinery - see RestoreDestinationModel.IsRefusedDestination's own
    /// doc comment for exactly why each of these four matters (in
    /// particular the safety-copy directory: applying INTO it could
    /// overwrite the very originals rule 2 just saved).
    /// </summary>
    private string[] ScratchDirs() => new[] { _stagedRoot, _gitStagingRoot, _driveTempRoot, _safetyBaseDir };

    private string? ResolveDestinationRoot() =>
        RestoreDestinationModel.ResolveDestinationRoot(_destinationKind, _liveRoot, _customDestinationRoot);

    /// <summary>
    /// True when <paramref name="destinationRoot"/> IS the live config root
    /// - not just when <see cref="_destinationKind"/> happens to be Live.
    /// Checked by full-path equality (case-insensitive, trailing separator
    /// trimmed) rather than trusting the enum alone, because a user can also
    /// reach live config by browsing to it by hand (see
    /// OnBrowseForDestination, which folds that case back into Live
    /// anyway) - this is the belt-and-braces version of the same check,
    /// used everywhere "is this actually live config" needs a real answer
    /// (the Claude-Code-running warning, and every "unmistakable" label).
    /// </summary>
    private bool DestinationIsLiveConfig(string destinationRoot) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(_liveRoot)),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets the combo's SelectedIndex to match <see cref="_destinationKind"/>
    /// and the Browse button's visibility to match it, WITHOUT re-firing
    /// <see cref="OnDestinationComboChanged"/> - used after this class's own
    /// code changes _destinationKind programmatically (e.g. folding a
    /// browsed live-config folder back into Live), where re-entering the
    /// change handler would be redundant at best and recursive at worst.
    /// </summary>
    private void SyncDestinationComboSelection()
    {
        _destinationCombo.SelectedIndexChanged -= OnDestinationComboChanged;
        _destinationCombo.SelectedIndex = _destinationKind == RestoreDestinationKind.Live ? 0 : 1;
        _destinationCombo.SelectedIndexChanged += OnDestinationComboChanged;
        _destinationBrowseButton.Visible = _destinationKind == RestoreDestinationKind.Custom;
    }

    /// <summary>
    /// Restore rule 6 made destination-aware, and the "unmistakable" label
    /// the brief asks for: live config is called out explicitly (and in the
    /// same amber this dialog already uses for warnings - see
    /// _sourceErrorLabel), since that is the one destination where a mistake
    /// is unrecoverable-by-convenience. A custom destination gets the plain
    /// subtle styling everything else in this panel uses - it is
    /// deliberately the LESS alarming of the two, since restoring into a
    /// scratch folder is the safe choice this feature exists to offer.
    /// </summary>
    private void UpdateDestinationLabel()
    {
        var root = ResolveDestinationRoot();
        if (root is null)
        {
            _destinationPathLabel.Text = "Choose a destination folder above before previewing.";
            _destinationPathLabel.ForeColor = _palette.SubtleFore;
            return;
        }

        if (DestinationIsLiveConfig(root))
        {
            _destinationPathLabel.Text = $"Restoring into your LIVE configuration: {root}";
            _destinationPathLabel.ForeColor = Theme.BandColor(Band.Amber);
        }
        else
        {
            _destinationPathLabel.Text = $"Restoring into: {root}  (not your live configuration)";
            _destinationPathLabel.ForeColor = _palette.SubtleFore;
        }
    }

    private void RefreshPreviewButtonEnabled() =>
        _previewButton.Enabled = !_busy && _snapshotList.SelectedItems.Count > 0 && ResolveDestinationRoot() is not null;

    private void OnDestinationComboChanged(object? sender, EventArgs e)
    {
        if (_busy) return;

        if (_destinationCombo.SelectedIndex == 1)
        {
            _destinationKind = RestoreDestinationKind.Custom;
            if (_customDestinationRoot is null)
                OnBrowseForDestination(); // may set a custom root, fold back to Live, or leave both null on cancel/refusal

            if (_customDestinationRoot is null && _destinationKind == RestoreDestinationKind.Custom)
                _destinationKind = RestoreDestinationKind.Live; // nothing usable was ever chosen - revert
        }
        else
        {
            _destinationKind = RestoreDestinationKind.Live;
        }

        SyncDestinationComboSelection();
        UpdateDestinationLabel();
        RefreshPreviewButtonEnabled();
    }

    /// <summary>
    /// Shared core for both the Browse button and "Another folder..." combo
    /// selection: shows a folder browser, and on a real pick either folds it
    /// into Live (if it turns out to literally be the live config root - see
    /// DestinationIsLiveConfig) or refuses it (if it overlaps one of this
    /// dialog's own scratch directories - see ScratchDirs/
    /// RestoreDestinationModel.IsRefusedDestination) or accepts it as the new
    /// custom destination. On cancel, or on refusal, _destinationKind/
    /// _customDestinationRoot are left exactly as they were - the caller
    /// (OnDestinationComboChanged or the Browse button's own click handler)
    /// is responsible for re-syncing the combo/label/Preview-button state
    /// afterward either way.
    /// </summary>
    private void OnBrowseForDestination()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to restore into. Nothing here is written until you apply.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = _customDestinationRoot ?? _liveRoot,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var picked = dialog.SelectedPath;

        if (DestinationIsLiveConfig(picked))
        {
            // Picking the live config folder by hand is just Live under a
            // different name - fold it into the Live selection so every
            // other destination-aware decision (the label, the
            // Claude-Code-running warning) reflects reality instead of
            // treating this as "Another folder...".
            _destinationKind = RestoreDestinationKind.Live;
            _customDestinationRoot = null;
            return;
        }

        if (RestoreDestinationModel.IsRefusedDestination(picked, ScratchDirs()))
        {
            MessageBox.Show(this,
                $"'{picked}' cannot be used as a restore destination - it is, or is inside, a folder this " +
                "dialog uses internally to stage and safeguard the restore itself. Choose a different folder.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _customDestinationRoot = picked;
        _destinationKind = RestoreDestinationKind.Custom;
    }

    // --- Panel 2: preview ----------------------------------------------------

    private Panel BuildPreviewPanel(Palette palette)
    {
        var panel = new Panel { Size = new Size(DialogWidth, ContentHeight), BackColor = palette.Back };
        var fullWidth = DialogWidth - Pad * 2;
        var y = Pad;

        // S10: restated here, at the top of the panel that is about to show
        // WHAT would change - the single most important fact for the user to
        // have read before ticking anything is WHERE it would change. Text
        // set in PopulatePreview from _destinationRootForApply (the frozen
        // value classify just ran against), not re-resolved live, so this
        // can never show a different destination than the one Apply will
        // actually use.
        // Fixed height, not AutoSize: text is set later at runtime
        // (PopulatePreview), once the actual destination path is known -
        // reserving a stable two-line height up front means a long path
        // wrapping to a second line can never push the banner/list below it
        // out of place, unlike an AutoSize label whose height is only
        // correct as of whenever PreferredHeight was last read.
        _previewDestinationLabel = NewFixedHeightDestinationLabel(palette, fullWidth);
        _previewDestinationLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_previewDestinationLabel);
        y += _previewDestinationLabel.Height + RowGap;

        // Restore rule 3, made visible: this is the one sentence in the whole
        // dialog whose entire job is to make "restore never deletes" a thing
        // the user actually reads, not just a property the engine happens to
        // have.
        // S10: worded to hold for either destination - "your live
        // configuration" would be wrong to read here when the destination
        // label right above says otherwise.
        var banner = NewSubtleLabel(
            "Tick the files to restore. Only New and Changed files can be selected - Identical files need " +
            "no action, and files that exist only in the destination (Live only) are never touched: " +
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
        var destination = _destinationRootForApply!; // set by OnPreviewClicked before this is ever called
        _previewDestinationLabel.Text = DestinationIsLiveConfig(destination)
            ? $"Restoring into your LIVE configuration: {destination}"
            : $"Restoring into: {destination}  (not your live configuration)";
        _previewDestinationLabel.ForeColor = DestinationIsLiveConfig(destination)
            ? Theme.BandColor(Band.Amber)
            : _palette.SubtleFore;

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
            $"{counts.LiveOnly} present only in the destination - left alone.";

        _applyButton.Enabled = !_busy && (selectedNew + selectedChanged) > 0;
    }

    // --- Panel 3: result -------------------------------------------------

    private Panel BuildResultPanel(Palette palette)
    {
        var panel = new Panel { Size = new Size(DialogWidth, ContentHeight), BackColor = palette.Back };
        var fullWidth = DialogWidth - Pad * 2;
        var y = Pad;

        // Fixed height (text set later, in PopulateResult), same reasoning
        // as _previewDestinationLabel below - a long result.Message must not
        // be able to wrap into and shift the destination label under it.
        _resultMessageLabel = NewSubtleLabel("", palette, fullWidth);
        _resultMessageLabel.ForeColor = palette.Fore;
        _resultMessageLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_resultMessageLabel);
        y += 40;

        // S10: which destination just got written is at least as important
        // to see after the fact as before - so this uses the same wording
        // and amber-for-live styling as the preview panel's own destination
        // label, from the same frozen _destinationRootForApply value. Fixed
        // height for the same reason as _previewDestinationLabel.
        _resultDestinationLabel = NewFixedHeightDestinationLabel(palette, fullWidth);
        _resultDestinationLabel.Location = new Point(Pad, y);
        panel.Controls.Add(_resultDestinationLabel);
        y += _resultDestinationLabel.Height + RowGap;

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

        var destination = _destinationRootForApply!; // set by OnPreviewClicked before Apply is ever reachable
        _resultDestinationLabel.Text = DestinationIsLiveConfig(destination)
            ? $"Written into your LIVE configuration: {destination}"
            : $"Written into: {destination}  (not your live configuration)";
        _resultDestinationLabel.ForeColor = DestinationIsLiveConfig(destination)
            ? Theme.BandColor(Band.Amber)
            : _palette.SubtleFore;

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
        _destinationCombo.Enabled = !busy;
        _destinationBrowseButton.Enabled = !busy;

        RefreshPreviewButtonEnabled();
        if (!busy) RefreshCounts(); // re-derives _applyButton.Enabled
    }

    // --- Long operations (off the UI thread) --------------------------------

    private async void OnPreviewClicked(object? sender, EventArgs e)
    {
        if (_busy || _snapshotList.SelectedItems.Count == 0)
            return;
        var snapshot = (RestoreSnapshot)_snapshotList.SelectedItems[0].Tag!;
        var destination = _selectedDestination!; // set whenever _destinations is non-empty - see the constructor

        // S10: resolved exactly once here, and everything for the rest of
        // this preview/apply cycle - classify below, and Apply once the user
        // confirms - uses THIS value (frozen into _destinationRootForApply
        // once classify actually succeeds), never a fresh re-resolution.
        // ResolveDestinationRoot can only be null if the Preview button was
        // somehow enabled without a destination chosen - RefreshPreviewButtonEnabled
        // is the guard that is supposed to prevent that; this is the
        // belt-and-braces backstop in case it did not.
        var destinationRoot = ResolveDestinationRoot();
        if (destinationRoot is null)
        {
            MessageBox.Show(this, "Choose a destination folder before previewing.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Defense in depth: the same refusal already ran when the folder was
        // picked (OnBrowseForDestination), but this is the point classify is
        // actually about to run against it, so it is re-checked here too -
        // mirrors RestoreApplier's own "re-check every rule at the point of
        // use, not just on the way in" discipline.
        if (RestoreDestinationModel.IsRefusedDestination(destinationRoot, ScratchDirs()))
        {
            MessageBox.Show(this,
                $"'{destinationRoot}' cannot be used as a restore destination - it is, or is inside, a folder " +
                "this dialog uses internally to stage and safeguard the restore itself. Choose a different folder.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true, "Materialising snapshot...");
        try
        {
            // Materialize's protectedRoot stays _liveRoot regardless of
            // destinationRoot above - it guards the STAGING write (always
            // into _stagedRoot) against ever landing in live config, which
            // is independent of where the user later chooses to classify/
            // apply against. See this class's own doc comment.
            var materialize = await Task.Run(() => MaterializeSnapshot(destination, snapshot.Id, _stagedRoot));

            if (IsDisposed) return;
            if (!materialize.Ok)
            {
                MessageBox.Show(this, materialize.Message, "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, DestinationIsLiveConfig(destinationRoot)
                ? "Comparing against your live configuration..."
                : "Comparing against the chosen destination folder...");
            // Fix round 1 (review): unlike Materialize (both RestoreZipSource
            // and RestoreGitSource wrap their own bodies and return Ok=false
            // on failure) and RestoreApplier.Apply (same), RestoreClassifier.
            // Classify has only PER-FILE IOException/UnauthorizedAccessException
            // handling, no top-level catch - anything else it throws would
            // otherwise escape this await, unwind out of this async void
            // handler, and reach Application.ThreadException, which shows an
            // error and calls Environment.Exit(1) - terminating before
            // FormClosed ever fires, leaving the materialised snapshot (a
            // plaintext copy of the user's Claude config) on disk
            // indefinitely instead of being cleaned up. Caught here and
            // reported the same way a materialize failure already is, so a
            // classify failure degrades to a message instead of a crash.
            IReadOnlyList<RestoreFileEntry> entries;
            try
            {
                entries = await Task.Run(() => RestoreClassifier.Classify(_stagedRoot, destinationRoot));
            }
            catch (Exception ex)
            {
                Log.Error($"RestoreDialog: classifying the staged snapshot failed unexpectedly: {ex}");
                if (!IsDisposed)
                {
                    MessageBox.Show(this,
                        $"Could not compare the staged snapshot against '{destinationRoot}': {ex.Message}\n\n" +
                        "See the log for details.",
                        "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }
            if (IsDisposed) return;

            _rows = RestoreDisplayModel.BuildRows(entries);
            _destinationRootForApply = destinationRoot; // frozen - see field's own doc comment
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

        // Set by OnPreviewClicked before Step.Preview (where Apply is the
        // only reachable action) is ever shown - see the field's own doc
        // comment for why this is a frozen value, not a fresh
        // ResolveDestinationRoot() call: classify already ran against
        // exactly this root, and Apply must use the identical one.
        var destinationRoot = _destinationRootForApply!;
        var isLiveConfig = DestinationIsLiveConfig(destinationRoot);

        // Restore rule 6: warn, never a hard block - the user may know
        // better than a name-based process check. S10: only meaningful when
        // restoring into live config - Claude Code holding a file open in
        // ~/.claude is the entire risk this warning exists to flag, and that
        // risk simply does not exist when applying into an unrelated scratch
        // folder, so showing it there would just be noise the user has to
        // click past on every custom-destination restore.
        if (isLiveConfig && ClaudeProcessDetector.AppearsRunning())
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
        // S10: the destination is named plainly and, for live config,
        // called out in capitals - the confirmation dialog is the last
        // point before anything is written, so this is the one place above
        // all others where "which destination is about to be written" must
        // be unmistakable.
        var destinationLine = isLiveConfig
            ? $"This will write {chosen.Count} file(s) into your LIVE CONFIGURATION at {destinationRoot}:"
            : $"This will write {chosen.Count} file(s) into {destinationRoot} (not your live configuration):";
        var confirmed = MessageBox.Show(this,
            $"{destinationLine}\n" +
            $"  {newCount} new file(s)\n" +
            $"  {changedCount} existing file(s) overwritten\n\n" +
            "Every file about to be overwritten is copied to a safety folder first, so this can be undone " +
            "by hand. Restore never deletes anything - files that exist only in the destination are left " +
            "alone.\n\nContinue?",
            "Confirm restore", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirmed != DialogResult.Yes)
            return;

        SetBusy(true, "Applying restore...");
        try
        {
            var result = await Task.Run(() => RestoreApplier.Apply(_stagedRoot, destinationRoot, chosen, _safetyBaseDir));
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
    /// the materialised snapshot, and every destination's own subdirectory
    /// of <see cref="_gitStagingRoot"/> (each a full, plaintext, multi-
    /// version copy of everything ever backed up to that GitHub destination)
    /// - is a second copy of (a subset of) the user's Claude config sitting
    /// on disk once this dialog is done with it. RestoreCleanup never throws
    /// and logs loudly on failure instead, so this is safe to call
    /// unconditionally from FormClosed. Deleting the ROOT recursively (not
    /// each destination's subdirectory individually) sweeps every
    /// destination's own scratch in one call - see the field-block comment
    /// on <see cref="_gitStagingRoot"/>/<see cref="_driveTempRoot"/>.
    /// <see cref="_driveTempRoot"/> is included too even though
    /// RestoreZipSource.Materialize already deletes the downloaded zip
    /// itself on every call - best effort, in case a call failed partway
    /// through and left it behind.
    /// </summary>
    private void CleanupStagingDirectories()
    {
        RestoreCleanup.DeleteStagingDirectory(_stagedRoot);
        RestoreCleanup.DeleteStagingDirectory(_gitStagingRoot);
        RestoreCleanup.DeleteStagingDirectory(_driveTempRoot);
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

    /// <summary>
    /// S10: a destination label whose Text is only known at runtime (set
    /// well after this panel's own layout has already positioned every
    /// control below it - see UpdateDestinationLabel/PopulatePreview/
    /// PopulateResult). Fixed at a reserved two-line height instead of
    /// AutoSize, so an unusually long destination path wrapping to a second
    /// line changes only what is drawn INSIDE this label's bounds, never the
    /// position of anything else in the panel - the overlap an AutoSize
    /// label would risk here, since its PreferredHeight at layout time
    /// reflects the empty string this label always starts with, not the
    /// real text set later.
    /// </summary>
    private static Label NewFixedHeightDestinationLabel(Palette palette, int width) => new()
    {
        AutoSize = false,
        Size = new Size(width, 32),
        Font = new Font("Segoe UI", 9f),
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

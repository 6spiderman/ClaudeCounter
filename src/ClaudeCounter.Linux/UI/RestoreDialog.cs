using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// The Linux build's "Restore from backup" dialog: a port of the Windows
/// build's WinForms RestoreDialog (src/ClaudeCounter/UI/RestoreDialog.cs),
/// with the same steps, wording and safety rules. Like the Windows dialog it
/// only sequences the flow and paints it; every safety decision - what counts
/// as New/Changed/Identical/LiveOnly, what the denylist and containment
/// checks refuse, where the safety copy goes, which destinations are refused
/// - is made by the shared engine in ClaudeCounter.Core (Restore/ and
/// Presentation/), never here. See the Windows file's doc comments for the
/// full reasoning behind each rule; the comments here only cover what
/// differs on Linux.
///
/// Flow (one window, three pages swapped via IsVisible):
///   1. Choose source + snapshot, and where to restore into (live config by
///      default, or another folder).
///   2. Materialise the chosen snapshot and classify it against the chosen
///      destination, off the UI thread.
///   3. Preview every classified file - only New/Changed can be ticked
///      (RestoreDisplayModel), Live-only files are never touched.
///   4. Apply - warns if Claude Code appears to be running (live config
///      only), confirms with counts, applies off the UI thread and shows the
///      result with the safety-copy path.
///
/// Differences from Windows, all deliberate:
///   - Listing snapshots also runs off the UI thread (Windows lists
///     synchronously, behind a wait cursor, so a smoke test can construct the
///     dialog already populated). A first `git clone` can take a while, and a
///     blocked Avalonia UI thread freezes the whole tray app. Listing only
///     writes restore-owned scratch, so the dialog can still be cancelled
///     while it runs; whatever it left behind is cleaned up when it finishes.
///   - The window refuses to close mid-materialise/classify/apply only for a
///     user close (<see cref="WindowCloseReason.WindowClosing"/>), never for a
///     logout/shutdown: Avalonia cancels the desktop's logout if any window
///     refuses to close. If the dialog does close while an operation is still
///     running, the staging directories are cleaned up again once that
///     operation finishes, so a plaintext copy of the user's config is not
///     left behind.
///   - "Is this the live config folder" uses the platform's own path
///     comparison (case-sensitive on Linux), because it decides which folder
///     a browsed pick is applied into: on Linux ~/.CLAUDE is a different
///     folder from ~/.claude and must not be folded into Live.
/// </summary>
public sealed class RestoreDialog : Window
{
    private enum Step { ChooseSnapshot, Preview, Result }

    private const double DialogWidth = 700;
    private const double LabelColumnWidth = 124;
    private const double ListHeight = 240;
    private const double DestinationLabelMinHeight = 38;

    private static readonly IBrush SubtleBrush = Brushes.Gray;
    private static readonly IBrush AmberBrush = new SolidColorBrush(BandPalette.BandColor(Band.Amber));
    private static readonly IBrush RedBrush = new SolidColorBrush(BandPalette.BandColor(Band.Red));

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IProcessRunner _runner;
    private readonly RestoreZipSource _zipSource;
    private readonly string _liveRoot;

    // Roots, not single scratch directories: a git destination's clone lives
    // at Path.Combine(_gitStagingRoot, destination.Id), a zip destination's
    // rclone download temp at Path.Combine(_driveTempRoot, destination.Id).
    // Same layout as the Windows dialog.
    private readonly string _gitStagingRoot;
    private readonly string _driveTempRoot;
    private readonly string _stagedRoot;
    private readonly string _safetyBaseDir;
    private readonly List<BackupDestination> _destinations = new();

    private BackupDestination? _selectedDestination;
    private IReadOnlyList<RestoreFileRow> _rows = Array.Empty<RestoreFileRow>();
    private List<FileItem> _fileItems = new();
    private bool _busy;
    // True while the current busy operation is one the dialog may still be
    // closed during (listing snapshots) - see this class's doc comment.
    private bool _closableWhileBusy;
    private bool _browsing;
    private bool _closed;
    private Step _currentStep = Step.ChooseSnapshot;

    // Live, editable "Restore into" selection (page 1), and that selection
    // frozen the moment a preview's classify succeeds - Apply always uses the
    // frozen value, so classify and apply can never disagree about where
    // "the destination" is. Same rules as the Windows dialog.
    private RestoreDestinationKind _destinationKind = RestoreDestinationKind.Live;
    private string? _customDestinationRoot;
    private string? _destinationRootForApply;

    private readonly Panel _contentHost;
    private readonly Control _choosePanel;
    private readonly Control _previewPanel;
    private readonly Control _resultPanel;

    private ComboBox? _sourceCombo;
    private ListBox _snapshotList = null!;
    private TextBlock _sourceErrorLabel = null!;

    private ComboBox _destinationCombo = null!;
    private Button _destinationBrowseButton = null!;
    private TextBlock _destinationPathLabel = null!;

    private ListBox _fileList = null!;
    private TextBlock _countsLabel = null!;
    private TextBlock _previewDestinationLabel = null!;

    private TextBlock _resultMessageLabel = null!;
    private TextBlock _resultDestinationLabel = null!;
    private TextBox _safetyCopyBox = null!;
    private TextBlock _skippedLabel = null!;

    private Button _backButton = null!;
    private Button _cancelButton = null!;
    private Button _previewButton = null!;
    private Button _applyButton = null!;
    private Button _closeButton = null!;
    private Control _statusPanel = null!;
    private TextBlock _statusLabel = null!;

    /// <summary>
    /// <paramref name="scratchRoot"/> is a test-only seam, exactly as on
    /// Windows: null (every real caller) uses the per-user local data folder
    /// (~/.local/share on Linux), created if it does not exist yet.
    /// </summary>
    public RestoreDialog(BackupConfig config, IProcessRunner runner, string? scratchRoot = null)
    {
        _liveRoot = config.SourceRoot;

        var local = scratchRoot ?? Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        _gitStagingRoot = Path.Combine(local, "ClaudeCounter", "restore-git-repo");
        _driveTempRoot = Path.Combine(local, "ClaudeCounter", "restore-drive-tmp");
        _stagedRoot = Path.Combine(local, "ClaudeCounter", "restore-staged");
        _safetyBaseDir = Path.Combine(local, "ClaudeCounter", "restore-safety");

        _runner = runner;
        _zipSource = new RestoreZipSource(runner);

        _destinations.AddRange(config.Destinations.Where(d => d.Enabled));

        Title = "Restore from backup";
        Width = DialogWidth;
        // Height follows the content (never a fixed height that could clip
        // the button row on a desktop with different fonts or scaling); the
        // pages are equalised once the window is open, so it does not jump
        // between steps.
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 3)) },
        });

        _choosePanel = BuildChoosePanel();
        _previewPanel = BuildPreviewPanel();
        _resultPanel = BuildResultPanel();
        _contentHost = new Panel
        {
            Margin = new Thickness(16),
            Children = { _choosePanel, _previewPanel, _resultPanel },
        };

        var bottomBar = BuildBottomBar();
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        Content = new DockPanel { Children = { bottomBar, _contentHost } };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled && CanCloseNow)
                Close();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            CleanupStagingDirectories();
        };

        ShowStep(Step.ChooseSnapshot);

        Opened += (_, _) =>
        {
            EqualizePageHeights();

            // Restore rule 1 made visible in the flow itself: nothing above
            // this point can reach ~/.claude - the first step only ever
            // lists and lets the user pick a snapshot.
            if (_destinations.Count == 0)
            {
                SetSourceError("No backup destination is enabled. Enable and configure a destination on the Backup tab first.");
            }
            else
            {
                _selectedDestination = _destinations[0];
                LoadSnapshots(_selectedDestination);
            }
        };
    }

    private bool CanCloseNow => !_busy || _closableWhileBusy;

    /// <summary>
    /// Restore is the most dangerous thing in this codebase, so a user close
    /// (the title bar's X, Alt+F4) must not tear the dialog down
    /// mid-materialise/classify/apply. Only that reason is refused: an OS
    /// logout/shutdown or application shutdown always closes, since a window
    /// that refuses those cancels the desktop's logout.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!CanCloseNow && e.CloseReason == WindowCloseReason.WindowClosing)
            e.Cancel = true;
        base.OnClosing(e);
    }

    // --- Per-destination source dispatch (same as Windows) ------------------

    private RestoreGitSource GitSourceFor(BackupDestination destination) =>
        new(_runner, Path.Combine(_gitStagingRoot, destination.Id));

    private RestoreListResult ListSnapshotsFor(BackupDestination destination) =>
        destination.Kind == DestinationKind.GitHub
            ? GitSourceFor(destination).ListSnapshots(destination.ToGitTarget())
            : _zipSource.ListSnapshots(destination.ToDriveTarget());

    private RestoreMaterializeResult MaterializeSnapshot(BackupDestination destination, string snapshotId, string destinationDir) =>
        destination.Kind == DestinationKind.GitHub
            ? GitSourceFor(destination).Materialize(destination.ToGitTarget(), snapshotId, destinationDir, _liveRoot)
            : _zipSource.Materialize(destination.ToDriveTarget(), snapshotId, Path.Combine(_driveTempRoot, destination.Id), destinationDir, _liveRoot);

    // --- Page 1: choose source + snapshot ------------------------------------

    private Control BuildChoosePanel()
    {
        var panel = new StackPanel { Spacing = 8 };

        if (_destinations.Count > 1)
        {
            _sourceCombo = new ComboBox
            {
                ItemsSource = _destinations.Select(d => d.Name).ToArray(),
                SelectedIndex = 0,
                Width = 200,
            };
            _sourceCombo.SelectionChanged += (_, _) =>
            {
                if (_busy || _sourceCombo.SelectedIndex < 0) return;
                _selectedDestination = _destinations[_sourceCombo.SelectedIndex];
                LoadSnapshots(_selectedDestination);
            };
            panel.Children.Add(LabeledRow("Restore from", _sourceCombo));
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = _destinations.Count == 1
                    ? $"Restoring from {_destinations[0].Name}."
                    : "No backup destination is enabled.",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // "Restore into" - live config (default) or a folder the user browses
        // to. On page 1 because classify (step 2) compares against it.
        _destinationCombo = new ComboBox
        {
            ItemsSource = new[] { "Live config", "Another folder..." },
            SelectedIndex = 0,
            Width = 200,
        };
        _destinationCombo.SelectionChanged += OnDestinationComboChanged;

        _destinationBrowseButton = new Button { Content = "Browse...", IsVisible = false };
        _destinationBrowseButton.Click += async (_, _) =>
        {
            if (_busy || _browsing) return;
            await BrowseForDestinationAsync();
            SyncDestinationComboSelection();
            UpdateDestinationLabel();
            RefreshPreviewButtonEnabled();
        };
        panel.Children.Add(LabeledRow("Restore into", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _destinationCombo, _destinationBrowseButton },
        }));

        _destinationPathLabel = NewDestinationLabel();
        panel.Children.Add(_destinationPathLabel);
        UpdateDestinationLabel();

        panel.Children.Add(Subtle("Available backups, newest first:"));

        _snapshotList = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<RestoreSnapshot>((snapshot, _) => Columns(
                new[] { new GridLength(150), GridLength.Star, new GridLength(100) },
                snapshot.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                snapshot.DisplayName,
                snapshot.SizeBytes is { } size ? FormatSize(size) : "-")),
        };
        _snapshotList.SelectionChanged += (_, _) => RefreshPreviewButtonEnabled();
        panel.Children.Add(ListWithHeader(
            Columns(new[] { new GridLength(150), GridLength.Star, new GridLength(100) }, "When", "Snapshot", "Size"),
            _snapshotList));

        _sourceErrorLabel = new TextBlock { Foreground = AmberBrush, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_sourceErrorLabel);

        return panel;
    }

    /// <summary>
    /// Lists snapshots for <paramref name="destination"/> and populates the
    /// snapshot list, off the UI thread (see this class's doc comment). Never
    /// lets an exception escape: the git source can already have cloned a
    /// full plaintext copy of every backed-up version into scratch by the
    /// time a later step throws, and the dialog must stay usable so that it
    /// still closes and cleans up normally.
    /// </summary>
    private async void LoadSnapshots(BackupDestination destination)
    {
        _snapshotList.ItemsSource = null;
        _sourceErrorLabel.Text = "";
        _previewButton.IsEnabled = false;

        SetBusy(true, "Listing available backups...", closable: true);
        RestoreListResult result;
        try
        {
            result = await Task.Run(() => ListSnapshotsFor(destination));
        }
        catch (Exception ex)
        {
            Log.Error($"RestoreDialog: listing snapshots failed unexpectedly: {ex}");
            if (_closed)
                CleanupStagingDirectories();
            else
                SetSourceError($"Could not list backups: {ex.Message}");
            return;
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }

        if (_closed)
        {
            // Closed while listing ran: the clone it may just have made is a
            // plaintext copy of the user's config - sweep it now.
            CleanupStagingDirectories();
            return;
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

        _snapshotList.ItemsSource = result.Snapshots;
    }

    private void SetSourceError(string message)
    {
        _sourceErrorLabel.Text = message;
    }

    // --- Destination choice ("Restore into") ---------------------------------

    /// <summary>
    /// Every folder this dialog writes restore-owned scratch data into. A
    /// destination that is, contains, or is inside any of them is refused
    /// (RestoreDestinationModel.IsRefusedDestination).
    /// </summary>
    private string[] ScratchDirs() => new[] { _stagedRoot, _gitStagingRoot, _driveTempRoot, _safetyBaseDir };

    private string? ResolveDestinationRoot() =>
        RestoreDestinationModel.ResolveDestinationRoot(_destinationKind, _liveRoot, _customDestinationRoot);

    /// <summary>
    /// True when <paramref name="destinationRoot"/> IS the live config root,
    /// by full-path equality rather than trusting the enum alone. Uses the
    /// platform's path comparison - see this class's doc comment.
    /// </summary>
    private bool DestinationIsLiveConfig(string destinationRoot) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(_liveRoot)),
        PathComparison);

    /// <summary>
    /// Sets the combo to match <see cref="_destinationKind"/> and the Browse
    /// button's visibility to match it, without re-firing the change handler.
    /// </summary>
    private void SyncDestinationComboSelection()
    {
        _destinationCombo.SelectionChanged -= OnDestinationComboChanged;
        _destinationCombo.SelectedIndex = _destinationKind == RestoreDestinationKind.Live ? 0 : 1;
        _destinationCombo.SelectionChanged += OnDestinationComboChanged;
        _destinationBrowseButton.IsVisible = _destinationKind == RestoreDestinationKind.Custom;
    }

    /// <summary>Live config is called out in amber; a custom folder gets the plain subtle styling.</summary>
    private void UpdateDestinationLabel()
    {
        var root = ResolveDestinationRoot();
        if (root is null)
        {
            _destinationPathLabel.Text = "Choose a destination folder above before previewing.";
            _destinationPathLabel.Foreground = SubtleBrush;
            return;
        }

        if (DestinationIsLiveConfig(root))
        {
            _destinationPathLabel.Text = $"Restoring into your LIVE configuration: {root}";
            _destinationPathLabel.Foreground = AmberBrush;
        }
        else
        {
            _destinationPathLabel.Text = $"Restoring into: {root}  (not your live configuration)";
            _destinationPathLabel.Foreground = SubtleBrush;
        }
    }

    private void RefreshPreviewButtonEnabled() =>
        _previewButton.IsEnabled = !_busy && !_browsing && _snapshotList.SelectedItem is RestoreSnapshot && ResolveDestinationRoot() is not null;

    private async void OnDestinationComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_busy || _browsing)
        {
            // The combo is disabled in both states; this only keeps it
            // honest if a change slips through anyway.
            SyncDestinationComboSelection();
            return;
        }

        if (_destinationCombo.SelectedIndex == 1)
        {
            _destinationKind = RestoreDestinationKind.Custom;
            if (_customDestinationRoot is null)
                await BrowseForDestinationAsync(); // may set a custom root, fold back to Live, or leave both null on cancel/refusal

            if (_customDestinationRoot is null && _destinationKind == RestoreDestinationKind.Custom)
                _destinationKind = RestoreDestinationKind.Live; // nothing usable was ever chosen - revert
        }
        else
        {
            _destinationKind = RestoreDestinationKind.Live;
        }

        if (_closed) return;
        SyncDestinationComboSelection();
        UpdateDestinationLabel();
        RefreshPreviewButtonEnabled();
    }

    /// <summary>
    /// Shared core for both the Browse button and the "Another folder..."
    /// combo entry: shows a folder picker, and on a real pick either folds it
    /// into Live (if it is the live config root), refuses it (if it overlaps
    /// one of this dialog's own scratch directories), or accepts it as the new
    /// custom destination. On cancel or refusal the selection is left exactly
    /// as it was; the caller re-syncs the combo/label/Preview state either way.
    /// </summary>
    private async Task BrowseForDestinationAsync()
    {
        _browsing = true;
        UpdateInteractiveControls();
        try
        {
            if (!StorageProvider.CanPickFolder)
            {
                await MessageDialog.ShowAsync(this, "This desktop does not provide a folder picker, so another folder cannot be chosen.", warning: true);
                return;
            }

            string? picked;
            IStorageFolder? start = null;
            try
            {
                start = await StorageProvider.TryGetFolderFromPathAsync(_customDestinationRoot ?? _liveRoot);
            }
            catch (Exception ex)
            {
                // Only the suggested start location - the picker still works without it.
                Log.Warn($"RestoreDialog: could not resolve the folder picker's start location: {ex.Message}");
            }

            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose a folder to restore into. Nothing here is written until you apply.",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                });
                if (folders.Count == 0)
                    return;
                picked = folders[0].TryGetLocalPath();
                foreach (var folder in folders)
                    folder.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"RestoreDialog: the folder picker failed: {ex}");
                if (!_closed)
                    await MessageDialog.ShowAsync(this, $"Could not open the folder picker: {ex.Message}", warning: true);
                return;
            }
            finally
            {
                start?.Dispose();
            }

            if (_closed)
                return;

            if (string.IsNullOrWhiteSpace(picked))
            {
                await MessageDialog.ShowAsync(this,
                    "That location is not a local folder, so it cannot be used as a restore destination. Choose a different folder.",
                    warning: true);
                return;
            }

            if (DestinationIsLiveConfig(picked))
            {
                // Picking the live config folder by hand is just Live under a
                // different name.
                _destinationKind = RestoreDestinationKind.Live;
                _customDestinationRoot = null;
                return;
            }

            if (RestoreDestinationModel.IsRefusedDestination(picked, ScratchDirs()))
            {
                await MessageDialog.ShowAsync(this,
                    $"'{picked}' cannot be used as a restore destination - it is, or is inside, a folder this " +
                    "dialog uses internally to stage and safeguard the restore itself. Choose a different folder.",
                    warning: true);
                return;
            }

            _customDestinationRoot = picked;
            _destinationKind = RestoreDestinationKind.Custom;
        }
        finally
        {
            _browsing = false;
            if (!_closed) UpdateInteractiveControls();
        }
    }

    // --- Page 2: preview -------------------------------------------------------

    /// <summary>One preview row plus its tick state, which outlives the row's (virtualised) checkbox.</summary>
    private sealed class FileItem
    {
        public FileItem(RestoreFileRow row)
        {
            Row = row;
            IsChecked = row.DefaultChecked;
        }

        public RestoreFileRow Row { get; }
        public bool IsChecked { get; set; }
    }

    private static readonly GridLength[] FileColumns =
        { new(28), new(90), GridLength.Star, new(100), new(100) };

    private Control BuildPreviewPanel()
    {
        var panel = new StackPanel { Spacing = 8 };

        // Where it would change, restated above what would change. Set in
        // PopulatePreview from the frozen _destinationRootForApply.
        _previewDestinationLabel = NewDestinationLabel();
        panel.Children.Add(_previewDestinationLabel);

        // Restore rule 3, made visible.
        panel.Children.Add(Subtle(
            "Tick the files to restore. Only New and Changed files can be selected - Identical files need " +
            "no action, and files that exist only in the destination (Live only) are never touched: " +
            "restore adds and overwrites, it never deletes."));

        _fileList = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<FileItem>((item, _) => BuildFileRow(item)),
        };
        var header = Columns(FileColumns, "", "Status", "Path", "Staged", "Live");
        panel.Children.Add(ListWithHeader(header, _fileList));

        _countsLabel = Subtle("");
        panel.Children.Add(_countsLabel);

        return panel;
    }

    private Control BuildFileRow(FileItem item)
    {
        var entry = item.Row.Entry;
        var row = Columns(FileColumns,
            "",
            StatusText(entry.Status),
            entry.RelativePath,
            entry.StagedSizeBytes is { } sb ? FormatSize(sb) : "-",
            entry.LiveSizeBytes is { } lb ? FormatSize(lb) : "-");
        if (!item.Row.Selectable)
        {
            foreach (var text in row.Children.OfType<TextBlock>())
                text.Foreground = SubtleBrush;
        }

        // Refuses the tick for anything RestoreDisplayModel says is not
        // selectable (Identical/LiveOnly) - disabled, and reverted if a tick
        // gets through anyway: the UI-side backstop for restore rule 3.
        var check = new CheckBox
        {
            IsChecked = item.IsChecked,
            IsEnabled = item.Row.Selectable,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            // Fluent's checkbox reserves a 32px-tall row around its 20px box;
            // the negative margin keeps the rows list-dense without clipping it.
            Margin = new Thickness(0, -6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) =>
        {
            if (!item.Row.Selectable)
            {
                if (check.IsChecked == true) check.IsChecked = false;
                return;
            }
            item.IsChecked = check.IsChecked == true;
            RefreshCounts();
        };
        Grid.SetColumn(check, 0);
        row.Children.Add(check);
        return row;
    }

    private void PopulatePreview()
    {
        var destination = _destinationRootForApply!; // set by OnPreviewClicked before this is ever called
        _previewDestinationLabel.Text = DestinationIsLiveConfig(destination)
            ? $"Restoring into your LIVE configuration: {destination}"
            : $"Restoring into: {destination}  (not your live configuration)";
        _previewDestinationLabel.Foreground = DestinationIsLiveConfig(destination) ? AmberBrush : SubtleBrush;

        _fileItems = _rows.Select(r => new FileItem(r)).ToList();
        _fileList.ItemsSource = _fileItems;
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
        var selected = _fileItems
            .Where(i => i.IsChecked && i.Row.Selectable)
            .Select(i => i.Row.Entry.Status)
            .ToList();
        var selectedNew = selected.Count(s => s == RestoreFileStatus.New);
        var selectedChanged = selected.Count(s => s == RestoreFileStatus.Changed);

        _countsLabel.Text =
            $"{selectedNew + selectedChanged} of {counts.New + counts.Changed} new/changed file(s) selected. " +
            $"{counts.Identical} identical (nothing to do). " +
            $"{counts.LiveOnly} present only in the destination - left alone.";

        _applyButton.IsEnabled = !_busy && (selectedNew + selectedChanged) > 0;
    }

    // --- Page 3: result -------------------------------------------------------

    private Control BuildResultPanel()
    {
        var panel = new StackPanel { Spacing = 8 };

        _resultMessageLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 32 };
        panel.Children.Add(_resultMessageLabel);

        // Which destination just got written, same wording/styling as the
        // preview page, from the same frozen value.
        _resultDestinationLabel = NewDestinationLabel();
        panel.Children.Add(_resultDestinationLabel);

        // Rule 2 made visible: the safety copy is the only undo, so its path
        // is shown in a selectable, copyable box.
        panel.Children.Add(new TextBlock { Text = "Safety copy of any overwritten files:" });
        _safetyCopyBox = new TextBox { IsReadOnly = true };
        panel.Children.Add(_safetyCopyBox);

        _skippedLabel = Subtle("");
        panel.Children.Add(_skippedLabel);

        return panel;
    }

    private void PopulateResult(RestoreApplyResult result)
    {
        _resultMessageLabel.Text = result.Ok
            ? result.Message
            : $"Restore did not finish cleanly: {result.Message}";
        if (result.Ok)
            _resultMessageLabel.ClearValue(TextBlock.ForegroundProperty);
        else
            _resultMessageLabel.Foreground = RedBrush;

        var destination = _destinationRootForApply!; // set by OnPreviewClicked before Apply is ever reachable
        _resultDestinationLabel.Text = DestinationIsLiveConfig(destination)
            ? $"Written into your LIVE configuration: {destination}"
            : $"Written into: {destination}  (not your live configuration)";
        _resultDestinationLabel.Foreground = DestinationIsLiveConfig(destination) ? AmberBrush : SubtleBrush;

        _safetyCopyBox.Text = result.SafetyCopyPath ?? "(none needed - no live file was overwritten)";

        _skippedLabel.Text = result.SkippedPaths.Count > 0
            ? $"{result.SkippedPaths.Count} requested file(s) were skipped - see the log for details."
            : "";
    }

    // --- Bottom bar / step management -------------------------------------------

    private Control BuildBottomBar()
    {
        _backButton = new Button { Content = "< Back", MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        _backButton.Click += (_, _) => { if (!_busy) ShowStep(Step.ChooseSnapshot); };

        _cancelButton = new Button { Content = "Cancel", MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        _cancelButton.Click += (_, _) => { if (CanCloseNow) Close(); };

        _previewButton = new Button { Content = "Preview selected snapshot...", IsEnabled = false };
        _previewButton.Click += OnPreviewClicked;

        _applyButton = new Button { Content = "Apply...", IsEnabled = false };
        _applyButton.Click += OnApplyClicked;

        _closeButton = new Button { Content = "Close", MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
        _closeButton.Click += (_, _) => Close();

        _statusLabel = new TextBlock { Foreground = SubtleBrush, VerticalAlignment = VerticalAlignment.Center };
        _statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Children =
            {
                new ProgressBar { IsIndeterminate = true, Width = 120, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center },
                _statusLabel,
            },
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _cancelButton, _previewButton, _applyButton, _closeButton },
        };
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _backButton, _statusPanel },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
            Padding = new Thickness(16, 10),
            Child = grid,
        };
    }

    private void ShowStep(Step step)
    {
        _currentStep = step;
        _choosePanel.IsVisible = step == Step.ChooseSnapshot;
        _previewPanel.IsVisible = step == Step.Preview;
        _resultPanel.IsVisible = step == Step.Result;

        _backButton.IsVisible = step == Step.Preview && !_busy;
        _cancelButton.IsVisible = step != Step.Result && CanCloseNow;
        _previewButton.IsVisible = step == Step.ChooseSnapshot;
        _applyButton.IsVisible = step == Step.Preview;
        _closeButton.IsVisible = step == Step.Result;
    }

    /// <summary>
    /// All three pages as tall as the tallest one, so the window keeps one
    /// size across steps (the Windows dialog has one fixed content height).
    /// Measured once the window is open: a page only has its real, styled
    /// size while it is visible.
    /// </summary>
    private void EqualizePageHeights()
    {
        var step = _currentStep;
        double tallest = 0;
        foreach (var s in new[] { Step.ChooseSnapshot, Step.Preview, Step.Result })
        {
            ShowStep(s);
            UpdateLayout();
            tallest = Math.Max(tallest, PanelFor(s).Bounds.Height);
        }
        _contentHost.MinHeight = tallest;
        ShowStep(step);
    }

    private Control PanelFor(Step step) => step switch
    {
        Step.Preview => _previewPanel,
        Step.Result => _resultPanel,
        _ => _choosePanel,
    };

    /// <summary>
    /// Toggles the busy/progress state shared by every long operation.
    /// Disables navigation so the window cannot be driven mid-operation, and
    /// re-derives each action button's enabled state from the current
    /// selection afterwards rather than just flipping it back on.
    /// <paramref name="closable"/> marks an operation the dialog may still be
    /// cancelled during (listing only).
    /// </summary>
    private void SetBusy(bool busy, string status = "", bool closable = false)
    {
        _busy = busy;
        _closableWhileBusy = busy && closable;
        _statusLabel.Text = status;
        _statusPanel.IsVisible = busy;
        _backButton.IsVisible = !busy && _currentStep == Step.Preview;
        _cancelButton.IsVisible = CanCloseNow && _currentStep != Step.Result;

        UpdateInteractiveControls();
        if (!busy) RefreshCounts(); // re-derives _applyButton.IsEnabled
        else _applyButton.IsEnabled = false;
    }

    private void UpdateInteractiveControls()
    {
        var idle = !_busy && !_browsing;
        _snapshotList.IsEnabled = !_busy;
        _fileList.IsEnabled = !_busy;
        if (_sourceCombo is not null) _sourceCombo.IsEnabled = idle;
        _destinationCombo.IsEnabled = idle;
        _destinationBrowseButton.IsEnabled = idle;
        RefreshPreviewButtonEnabled();
    }

    // --- Long operations (off the UI thread) ------------------------------------

    private async void OnPreviewClicked(object? sender, RoutedEventArgs e)
    {
        if (_busy || _browsing || _snapshotList.SelectedItem is not RestoreSnapshot snapshot)
            return;
        var destination = _selectedDestination!; // set whenever _destinations is non-empty

        // Resolved exactly once here and frozen into _destinationRootForApply
        // once classify succeeds; Apply uses that, never a fresh resolution.
        var destinationRoot = ResolveDestinationRoot();
        if (destinationRoot is null)
        {
            await MessageDialog.ShowAsync(this, "Choose a destination folder before previewing.", warning: true);
            return;
        }
        // Defense in depth: re-checked at the point classify is about to run.
        if (RestoreDestinationModel.IsRefusedDestination(destinationRoot, ScratchDirs()))
        {
            await MessageDialog.ShowAsync(this,
                $"'{destinationRoot}' cannot be used as a restore destination - it is, or is inside, a folder " +
                "this dialog uses internally to stage and safeguard the restore itself. Choose a different folder.",
                warning: true);
            return;
        }

        SetBusy(true, "Materialising snapshot...");
        try
        {
            // Materialize's protectedRoot stays _liveRoot regardless of
            // destinationRoot - it guards the STAGING write against ever
            // landing in live config.
            RestoreMaterializeResult materialize;
            try
            {
                materialize = await Task.Run(() => MaterializeSnapshot(destination, snapshot.Id, _stagedRoot));
            }
            catch (Exception ex)
            {
                // Both sources already turn their own failures into Ok=false;
                // this only keeps anything else from crashing the app (and
                // skipping cleanup) out of an async void handler.
                Log.Error($"RestoreDialog: materialising the snapshot failed unexpectedly: {ex}");
                if (_closed)
                    CleanupStagingDirectories();
                else
                    await MessageDialog.ShowAsync(this, $"Could not materialise the snapshot: {ex.Message}\n\nSee the log for details.", warning: true);
                return;
            }

            if (_closed)
            {
                CleanupStagingDirectories();
                return;
            }
            if (!materialize.Ok)
            {
                await MessageDialog.ShowAsync(this, materialize.Message, warning: true);
                return;
            }

            SetBusy(true, DestinationIsLiveConfig(destinationRoot)
                ? "Comparing against your live configuration..."
                : "Comparing against the chosen destination folder...");
            // RestoreClassifier.Classify has only per-file IO handling, no
            // top-level catch - anything else it throws is reported here
            // instead of escaping this async void handler.
            IReadOnlyList<RestoreFileEntry> entries;
            try
            {
                entries = await Task.Run(() => RestoreClassifier.Classify(_stagedRoot, destinationRoot));
            }
            catch (Exception ex)
            {
                Log.Error($"RestoreDialog: classifying the staged snapshot failed unexpectedly: {ex}");
                if (_closed)
                {
                    CleanupStagingDirectories();
                }
                else
                {
                    await MessageDialog.ShowAsync(this,
                        $"Could not compare the staged snapshot against '{destinationRoot}': {ex.Message}\n\n" +
                        "See the log for details.",
                        warning: true);
                }
                return;
            }
            if (_closed)
            {
                CleanupStagingDirectories();
                return;
            }

            _rows = RestoreDisplayModel.BuildRows(entries);
            _destinationRootForApply = destinationRoot; // frozen - see the field's comment
            PopulatePreview();
            ShowStep(Step.Preview);
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    private async void OnApplyClicked(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _fileItems
            .Where(i => i.IsChecked && i.Row.Selectable)
            .Select(i => i.Row.Entry)
            .ToList();
        if (chosen.Count == 0)
            return;

        // The frozen value classify ran against - Apply must use the same one.
        var destinationRoot = _destinationRootForApply!;
        var isLiveConfig = DestinationIsLiveConfig(destinationRoot);

        // Restore rule 6: warn, never a hard block - and only when restoring
        // into live config, the only place a running Claude Code matters.
        if (isLiveConfig && ClaudeProcessDetector.AppearsRunning())
        {
            var proceedPastWarning = await MessageDialog.ConfirmAsync(this,
                "Claude Code appears to be running. Restoring now could overwrite files it currently has " +
                "open or is using mid-session, which can corrupt its state.\n\nContinue anyway?",
                warning: true);
            if (!proceedPastWarning || _closed)
                return;
        }

        var newCount = chosen.Count(c => c.Status == RestoreFileStatus.New);
        var changedCount = chosen.Count(c => c.Status == RestoreFileStatus.Changed);
        // The last point before anything is written: the destination is
        // named plainly and, for live config, called out in capitals.
        var destinationLine = isLiveConfig
            ? $"This will write {chosen.Count} file(s) into your LIVE CONFIGURATION at {destinationRoot}:"
            : $"This will write {chosen.Count} file(s) into {destinationRoot} (not your live configuration):";
        var confirmed = await MessageDialog.ConfirmAsync(this,
            $"{destinationLine}\n" +
            $"  {newCount} new file(s)\n" +
            $"  {changedCount} existing file(s) overwritten\n\n" +
            "Every file about to be overwritten is copied to a safety folder first, so this can be undone " +
            "by hand. Restore never deletes anything - files that exist only in the destination are left " +
            "alone.\n\nContinue?");
        if (!confirmed || _closed || _busy)
            return;

        SetBusy(true, "Applying restore...");
        try
        {
            RestoreApplyResult result;
            try
            {
                result = await Task.Run(() => RestoreApplier.Apply(_stagedRoot, destinationRoot, chosen, _safetyBaseDir));
            }
            catch (Exception ex)
            {
                // RestoreApplier.Apply returns Ok=false for its own failures;
                // this only keeps anything else out of an async void handler.
                Log.Error($"RestoreDialog: applying the restore failed unexpectedly: {ex}");
                if (!_closed)
                {
                    await MessageDialog.ShowAsync(this,
                        $"Restore did not finish cleanly: {ex.Message}\n\nSee the log for details - any safety copy already made is under '{_safetyBaseDir}'.",
                        warning: true);
                }
                return;
            }
            if (_closed)
            {
                CleanupStagingDirectories();
                return;
            }

            PopulateResult(result);
            ShowStep(Step.Result);
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    /// <summary>
    /// Deletes every restore-owned scratch directory (the materialised
    /// snapshot, every destination's git clone, the rclone download temp) -
    /// each a plaintext copy of (a subset of) the user's Claude config. Never
    /// the safety-copy folder: that is the undo. RestoreCleanup never throws.
    /// </summary>
    private void CleanupStagingDirectories()
    {
        RestoreCleanup.DeleteStagingDirectory(_stagedRoot);
        RestoreCleanup.DeleteStagingDirectory(_gitStagingRoot);
        RestoreCleanup.DeleteStagingDirectory(_driveTempRoot);
    }

    // --- Small control factories ------------------------------------------------

    private static TextBlock Subtle(string text) => new()
    {
        Text = text,
        Foreground = SubtleBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>
    /// A destination label whose text is only known at runtime. Reserves two
    /// lines, so a long path that wraps does not shift the rest of the page.
    /// </summary>
    private static TextBlock NewDestinationLabel() => new()
    {
        Foreground = SubtleBrush,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = DestinationLabelMinHeight,
    };

    private static Control LabeledRow(string label, Control input)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelColumnWidth},*") };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(input, 1);
        input.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(text);
        grid.Children.Add(input);
        return grid;
    }

    /// <summary>A row of single-line, ellipsised text cells in fixed/star columns (a list row or its header).</summary>
    private static Grid Columns(GridLength[] widths, params string[] cells)
    {
        var grid = new Grid();
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition(width));
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i].Length == 0) continue;
            var text = new TextBlock
            {
                Text = cells[i],
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            ToolTip.SetTip(text, cells[i]);
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }

    private static Control ListWithHeader(Grid header, ListBox list)
    {
        foreach (var text in header.Children.OfType<TextBlock>())
        {
            text.FontWeight = FontWeight.SemiBold;
            text.Foreground = SubtleBrush;
            ToolTip.SetTip(text, null);
        }
        // Lines the header up with the rows' content: the list's border plus
        // its items' left padding (the ListBoxItem style in the constructor).
        header.Margin = new Thickness(9, 0, 9, 4);
        list.Height = ListHeight;
        list.Background = Brushes.Transparent;
        return new StackPanel
        {
            Children =
            {
                header,
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1),
                    Child = list,
                },
            },
        };
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

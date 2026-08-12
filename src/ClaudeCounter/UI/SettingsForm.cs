using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using ClaudeBackup;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

/// <summary>
/// Settings dialog: a custom themed tab strip (General / Alerts / Backup) over a
/// fixed-size content area, with a bottom OK/Cancel bar that never scrolls away.
/// Replaces the old single scrolling TableLayoutPanel - every row here is placed
/// at an explicit Location rather than a grid cell, and the dialog's ClientSize is
/// derived from the tallest tab's actual content height (see the constructor),
/// which is what guarantees no page can ever need AutoScroll.
/// </summary>
public sealed class SettingsForm : Form
{
    // Single source of truth for the frequency combo: index i's Display is
    // shown in the UI and its Value is what is persisted to ScheduleConfig.
    // Keeping them paired (rather than two parallel arrays matched only by
    // SelectedIndex) means reordering an entry cannot silently corrupt saved
    // config.
    private static readonly (string Value, string Display)[] BackupFrequencies =
    [
        ("daily", "Daily"),
        ("weekly", "Weekly"),
        ("hourly", "Hourly"),
    ];

    /// <summary>
    /// One entry per item in the Backup tab's "Back up to" selector (S16
    /// design: named backup destinations, replacing the old two nested
    /// dropdowns - see BuildBackupPage's own doc comment). GitHub is its own
    /// row; the other five all describe the SAME underlying Drive slot
    /// (DriveTarget has one Transport/FolderPath/RcloneRemote/SyncProvider,
    /// not five) - picking one of them just picks what that slot's Transport
    /// and, for the sync-folder transport, SyncProvider should be. Kept as a
    /// single source of truth (index i's label IS item i in the ComboBox)
    /// rather than parallel arrays matched only by SelectedIndex, mirroring
    /// BackupFrequencies just above.
    /// </summary>
    private readonly record struct BackupDestinationOption(string Label, bool IsGithub, DriveTransport Transport, SyncProvider Provider);

    private static readonly BackupDestinationOption[] BackupDestinationOptions =
    [
        new("GitHub", true, DriveTransport.Rclone, SyncProvider.Other),
        new("Google Drive (sync folder)", false, DriveTransport.SyncFolder, SyncProvider.GoogleDrive),
        new("OneDrive (sync folder)", false, DriveTransport.SyncFolder, SyncProvider.OneDrive),
        new("Dropbox (sync folder)", false, DriveTransport.SyncFolder, SyncProvider.Dropbox),
        new("NAS / network share", false, DriveTransport.SyncFolder, SyncProvider.Nas),
        new("rclone remote (advanced)", false, DriveTransport.Rclone, SyncProvider.Other),
    ];

    /// <summary>
    /// Picks which item of <see cref="BackupDestinationOptions"/> the "Back
    /// up to" selector should open on for <paramref name="config"/>. Public
    /// and static (no Form) for the same reason as <see
    /// cref="HasEmbeddedCredential"/> further down: this project's tests
    /// never construct a Form except via the dedicated STA smoke-test
    /// helper. Mirrors the old selector's own tie-break - GitHub wins when
    /// it is enabled, or when neither destination is enabled - and then, for
    /// Drive, reads its Transport (and, for the sync-folder transport, its
    /// SyncProvider - inferring one from FolderPath via <see
    /// cref="SyncProviderInference"/> when SyncProvider is still Other, e.g.
    /// a backup.json written by the pre-S16 build) to land on the matching
    /// item.
    /// </summary>
    public static int InitialDestinationIndex(BackupConfig config)
    {
        if (config.Github.Enabled || !config.Drive.Enabled)
            return 0;

        if (config.Drive.Transport != DriveTransport.SyncFolder)
            return 5; // rclone remote (advanced)

        var provider = config.Drive.SyncProvider == SyncProvider.Other
            ? SyncProviderInference.InferFromPath(config.Drive.FolderPath)
            : config.Drive.SyncProvider;

        return provider switch
        {
            SyncProvider.OneDrive => 2,
            SyncProvider.Dropbox => 3,
            SyncProvider.Nas => 4,
            // GoogleDrive, or Other (an unrecognised path) - Google Drive
            // was the original combined "Sync folder" transport's first/
            // default entry, so it is the least surprising fallback rather
            // than inventing a sixth "Custom folder" item the design brief
            // never asked for.
            _ => 1,
        };
    }

    // Shared explicitly rather than relying on ambient Font inheritance: pages
    // are measured (PreferredHeight / GetPreferredSize) before they are ever
    // attached to the form's control tree, and an unattached control's ambient
    // Font falls back to Control.DefaultFont, not the Form's. Measuring against
    // the wrong font would under- or over-estimate row heights. Setting Font
    // explicitly on every measured control keeps construction-time measurement
    // and paint-time rendering using the same metrics.
    private static readonly Font BaseFont = new("Segoe UI", 9f);
    private static readonly Font TabFont = new("Segoe UI Semibold", 9.5f);

    private const int DialogWidth = 560;
    private const int TabStripHeight = 44;
    private const int BottomBarHeight = 52;
    private const int TabButtonWidth = 100;
    private const int TabGap = 6;
    private const int PagePadX = 20;
    private const int PageTopY = 18;
    private const int FieldX = 230;
    private const int RowGap = 8;
    private const int LabelYOffset = 4;
    private const int InfoButtonSize = 16;

    // No longer `readonly`: these are now assigned from the per-tab Build*Page
    // helper methods rather than directly in the constructor body, and C# only
    // allows readonly-field assignment from within the constructor itself. Each
    // is still assigned exactly once, before the constructor returns.
    private ComboBox _intervalCombo = null!;
    private NumericUpDown _warnInput = null!;
    private NumericUpDown _criticalInput = null!;
    private CheckBox _autostartCheck = null!;
    private CheckBox _updateCheck = null!;
    private CheckBox _warnAlerts = null!;
    private CheckBox _criticalAlerts = null!;
    private CheckBox _maxedAlerts = null!;
    private CheckBox _alertFiveHour = null!;
    private CheckBox _alertSevenDay = null!;
    private CheckBox _alertOpus = null!;
    private CheckBox _alertSonnet = null!;
    private ComboBox _placement = null!;
    private NumericUpDown _autoDismissInput = null!;
    private NumericUpDown _alertRepeatInput = null!;

    // Only created when BackupTaskManager.WorkerAvailable() - the Backup tab is
    // entirely absent (fields stay null, and no tab button is created) when
    // ClaudeBackup.exe is not installed next to the tray exe. This project has
    // no ProjectReference to ClaudeBackup.csproj - the ClaudeBackup types used
    // below (BackupConfig, ScheduleConfig, ...) live in ClaudeCounter.Shared
    // instead. Do not add a reference to ClaudeBackup.csproj here; it drags the
    // worker's RID-specific publish graph into the tray's single-file publish
    // and breaks it.
    // S5, fix round 1: GitHub's and Drive's connection fields plus their own
    // Include/Exclude now live in two separate blocks (see BuildGithubBlock /
    // BuildDriveBlock) that are both always constructed, with only one ever
    // Visible - _backupDestination (S16: one flat "Back up to" list of named
    // destinations) controls which. Every field below is therefore a
    // genuinely separate Control per destination (not a single shared
    // control whose content gets swapped), so OnSaveBackupSchedule can read
    // both destinations' values directly at any time regardless of which
    // block currently happens to be on screen.
    private CheckBox? _backupGithubEnabled;
    private TextBox? _backupGithubUrl;
    private TextBox? _backupGithubBranch;
    private TextBox? _backupGithubInclude;
    private TextBox? _backupGithubExclude;
    private CheckBox? _backupDriveEnabled;
    private TextBox? _backupDriveFolderPath;
    private TextBox? _backupDriveRemote;
    private TextBox? _backupDriveInclude;
    private TextBox? _backupDriveExclude;
    private ComboBox? _backupDestination;
    private Panel? _backupGithubBlock;
    private Panel? _backupDriveBlock;

    // S16: replaces the old nested "Transport" dropdown - the top-level
    // _backupDestination selector now encodes the Drive slot's transport AND
    // (for the sync-folder transport) which named provider it is directly,
    // so there is no separate control to read either back from. But
    // _backupDestination's own SelectedIndex is not enough on its own to
    // recover "what should be saved for Drive" at save time, because GitHub
    // is one of the SAME selector's items now - picking GitHub must not
    // forget whatever the Drive slot was last set to. These two fields are
    // that memory: seeded from the loaded config, and updated only when the
    // user picks one of the selector's non-GitHub items (see
    // OnBackupDestinationChanged) - never touched while GitHub is selected,
    // so switching to GitHub and back leaves them exactly as they were.
    private DriveTransport _backupDriveTransportSelection;
    private SyncProvider _backupDriveSyncProviderSelection;
    // S14b: the sync-folder-path row and the rclone-remote row occupy the
    // SAME y-range within the Drive block and are never both visible at
    // once - same "two blocks, one Visible" swap _backupGithubBlock/
    // _backupDriveBlock already use one level up, just nested one level
    // deeper (within Drive's own block) so adding the sync-folder transport
    // costs no extra dialog height beyond whichever row is taller. See
    // BuildDriveBlock.
    private Panel? _backupDriveFolderRow;
    private Panel? _backupDriveRcloneRow;
    private ComboBox? _backupFrequency;
    private TextBox? _backupTime;

    // S7/S8: the schedule-robustness and Drive-retention settings edited by
    // BackupAdvancedDialog. These live as plain fields on the form (like
    // every other Backup tab value) rather than on a live-bound control,
    // because the dialog that edits them is not always open - seeded from
    // the loaded BackupConfig in BuildBackupPage, mutated only when the
    // Advanced dialog closes with OK (see OnOpenAdvancedDialog), and read
    // back into BackupConfig by OnSaveBackupSchedule exactly like every
    // other field on this form.
    private bool _scheduleStartWhenAvailable = true;
    private bool _scheduleRunOnlyIfNetworkAvailable = true;
    private bool _scheduleDisallowStartIfOnBatteries;
    private bool _scheduleStopIfGoingOnBatteries;
    private bool _scheduleRestartOnFailure = true;
    private int _scheduleRestartIntervalMinutes = 15;
    private int _scheduleRestartCount = 3;
    // S11b: same "field seeded in BuildBackupPage, mutated only via the
    // Advanced dialog's OK, read back in OnSaveBackupSchedule" shape as every
    // other schedule field above.
    private int _scheduleBackupStaleAfterDays = 3;
    private int? _driveKeepLastCount;
    private int? _driveDeleteOlderThanDays;

    // Backs every per-field (i) popup on the Backup tab. A single shared
    // instance (not one per button) because ToolTip.Show already positions
    // and dismisses independently per call; only created when the Backup tab
    // is (BackupTaskManager.WorkerAvailable()), and disposed in Dispose below
    // since it is a Component, not a Control, and would otherwise outlive the
    // form's own Controls.Clear()-driven cleanup.
    private ToolTip? _helpTip;

    // Test-isolation seam (S17-fix): every BackupConfig.Load/Save call in
    // this class goes through this field instead of calling
    // BackupConfig.DefaultPath() directly. null (every real caller) resolves
    // to the real %APPDATA%\ClaudeCounter\backup.json, exactly as before -
    // production wiring is unchanged. SettingsFormSmokeTests constructs this
    // form on an STA thread purely to prove it does not throw, but
    // BuildBackupPage unconditionally calls BackupConfig.Load, and Load
    // SAVES the file when it migrates a stale BackupConfigVersion - so
    // without this seam, running the test suite on a machine with a real,
    // stale backup.json silently rewrites it. A test that wants isolation
    // passes a GUID-suffixed temp path here instead.
    private readonly string _backupConfigPath;

    public SettingsForm(AppSettings current, string? backupConfigPath = null)
    {
        _backupConfigPath = backupConfigPath ?? BackupConfig.DefaultPath();
        var palette = Theme.Current();

        Text = "ClaudeCounter Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = BaseFont;
        ShowInTaskbar = true;
        Icon = Shell.AppIcon();
        BackColor = palette.Back;
        ForeColor = palette.Fore;

        var backupAvailable = BackupTaskManager.WorkerAvailable();

        var tabStrip = new Panel { Dock = DockStyle.Top, Height = TabStripHeight, BackColor = palette.BarBack };
        tabStrip.Controls.Add(new Panel
        {
            Location = new Point(0, TabStripHeight - 1),
            Size = new Size(DialogWidth, 1),
            BackColor = palette.Border,
        });

        var bottomBar = BuildBottomBar(palette, out var okButton, out var cancelButton);
        var contentHost = new Panel { Dock = DockStyle.Fill, BackColor = palette.Back };

        // WinForms resolves Dock by walking the Controls collection from the
        // LAST index backwards, so whichever control is added LAST is laid out
        // FIRST and gets first claim on the client area - and, being at the
        // lowest z-order/highest index, paints UNDER everything added before
        // it. Fill must therefore be added first (so Top/Bottom, laid out
        // after it, carve their strips out of what is left) and it must also
        // end up visually behind the edge bars, which this same ordering
        // achieves for free.
        Controls.Add(contentHost);
        Controls.Add(bottomBar);
        Controls.Add(tabStrip);

        var tabs = new List<(TabButton Button, Panel Page)>();

        void SelectTab(TabButton selected)
        {
            foreach (var (button, page) in tabs)
            {
                var active = ReferenceEquals(button, selected);
                button.IsActive = active;
                button.Invalidate();
                page.Visible = active;
            }
        }

        void AddTab(string label, Panel page)
        {
            var index = tabs.Count;
            var tabButton = new TabButton(label, palette)
            {
                Location = new Point(PagePadX + index * (TabButtonWidth + TabGap), 0),
                Size = new Size(TabButtonWidth, TabStripHeight - 1),
            };
            tabButton.Click += (_, _) => SelectTab(tabButton);
            // Bonus, not required: Left/Right cycles focus (and selection)
            // between tabs without needing to Tab back out to the strip.
            tabButton.KeyDown += (_, args) =>
            {
                if (args.KeyCode is not (Keys.Left or Keys.Right))
                    return;
                var from = tabs.FindIndex(t => ReferenceEquals(t.Button, tabButton));
                var delta = args.KeyCode == Keys.Right ? 1 : -1;
                var next = tabs[(from + delta + tabs.Count) % tabs.Count].Button;
                SelectTab(next);
                next.Focus();
                args.Handled = true;
            };
            tabStrip.Controls.Add(tabButton);
            tabs.Add((tabButton, page));
            contentHost.Controls.Add(page);
        }

        var (generalPage, generalHeight) = BuildGeneralPage(current, palette);
        var (alertsPage, alertsHeight) = BuildAlertsPage(current, palette);
        AddTab("General", generalPage);
        AddTab("Alerts", alertsPage);
        var contentHeight = Math.Max(generalHeight, alertsHeight);

        if (backupAvailable)
        {
            var (backupPage, backupHeight) = BuildBackupPage(palette);
            AddTab("Backup", backupPage);
            contentHeight = Math.Max(contentHeight, backupHeight);
        }

        // The dialog's height is derived FROM the tallest tab's actual measured
        // content, not the other way around - contentHost (and every page inside
        // it, since they Dock = Fill within it) is always exactly tall enough
        // for its tallest occupant. That is what makes AutoScroll unnecessary:
        // there is no page whose content can exceed the area given to it.
        ClientSize = new Size(DialogWidth, TabStripHeight + contentHeight + BottomBarHeight);

        SelectTab(tabs[0].Button);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private Panel BuildBottomBar(Palette palette, out Button okButton, out Button cancelButton)
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = palette.BarBack };
        bar.Controls.Add(new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(DialogWidth, 1),
            BackColor = palette.Border,
        });

        cancelButton = NewDialogButton("Cancel", palette);
        cancelButton.DialogResult = DialogResult.Cancel;

        okButton = NewDialogButton("OK", palette);
        okButton.Click += OnOk;

        var cancelX = DialogWidth - PagePadX - cancelButton.Width;
        var okX = cancelX - 8 - okButton.Width;
        var btnY = (BottomBarHeight - okButton.Height) / 2;
        cancelButton.Location = new Point(cancelX, btnY);
        okButton.Location = new Point(okX, btnY);

        bar.Controls.Add(cancelButton);
        bar.Controls.Add(okButton);
        return bar;
    }

    /// <summary>Update frequency, thresholds, autostart, auto-update.</summary>
    private (Panel Page, int Height) BuildGeneralPage(AppSettings current, Palette palette)
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = palette.Back, Visible = false };
        var y = PageTopY;

        page.Controls.Add(NewFieldLabel("Update frequency", palette, y));
        _intervalCombo = NewCombo(palette, 130);
        _intervalCombo.Location = new Point(FieldX, y);
        foreach (var minutes in AppSettings.IntervalPresets)
            _intervalCombo.Items.Add($"{minutes} min");
        _intervalCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.IntervalPresets, current.PollIntervalMinutes));
        page.Controls.Add(_intervalCombo);
        y += _intervalCombo.Height + RowGap;

        var note = NewSubtleLabel(
            "Intervals under 3 min may be rate limited; the app backs off automatically.",
            palette, DialogWidth - PagePadX * 2);
        note.Location = new Point(PagePadX, y);
        page.Controls.Add(note);
        y += note.PreferredHeight + RowGap;

        page.Controls.Add(NewFieldLabel("Warn threshold (%)", palette, y));
        _warnInput = NewNumeric(palette, 1, 99, current.WarnThreshold);
        _warnInput.Location = new Point(FieldX, y);
        page.Controls.Add(_warnInput);
        y += _warnInput.Height + RowGap;

        page.Controls.Add(NewFieldLabel("Critical threshold (%)", palette, y));
        _criticalInput = NewNumeric(palette, 2, 100, current.CriticalThreshold);
        _criticalInput.Location = new Point(FieldX, y);
        page.Controls.Add(_criticalInput);
        y += _criticalInput.Height + RowGap;

        _autostartCheck = NewCheckBox("Start with Windows", current.AutostartEnabled, palette);
        _autostartCheck.Location = new Point(PagePadX, y);
        page.Controls.Add(_autostartCheck);
        y += _autostartCheck.Height + RowGap;

        _updateCheck = NewCheckBox("Check for updates automatically", current.CheckForUpdates, palette);
        _updateCheck.Location = new Point(PagePadX, y);
        page.Controls.Add(_updateCheck);
        y += _updateCheck.Height + RowGap;

        return (page, y + 10);
    }

    /// <summary>Popup toggles, the four per-window checkboxes (two columns), placement.</summary>
    private (Panel Page, int Height) BuildAlertsPage(AppSettings current, Palette palette)
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = palette.Back, Visible = false };
        var y = PageTopY;
        const int col2X = 290;

        _warnAlerts = NewCheckBox("Popup when a window hits the warn threshold", current.WarnAlertsEnabled, palette);
        _warnAlerts.Location = new Point(PagePadX, y);
        page.Controls.Add(_warnAlerts);
        y += _warnAlerts.Height + RowGap;

        _criticalAlerts = NewCheckBox("Popup when a window hits the critical threshold", current.CriticalAlertsEnabled, palette);
        _criticalAlerts.Location = new Point(PagePadX, y);
        page.Controls.Add(_criticalAlerts);
        y += _criticalAlerts.Height + RowGap;

        _maxedAlerts = NewCheckBox("Popup when a window hits 100%", current.MaxedAlertsEnabled, palette);
        _maxedAlerts.Location = new Point(PagePadX, y);
        page.Controls.Add(_maxedAlerts);
        y += _maxedAlerts.Height + RowGap;

        _alertFiveHour = NewCheckBox("5-hour session", current.AlertFiveHour, palette);
        _alertFiveHour.Location = new Point(PagePadX, y);
        page.Controls.Add(_alertFiveHour);

        _alertSevenDay = NewCheckBox("Weekly (all models)", current.AlertSevenDay, palette);
        _alertSevenDay.Location = new Point(col2X, y);
        page.Controls.Add(_alertSevenDay);
        y += _alertFiveHour.Height + RowGap;

        _alertOpus = NewCheckBox("Weekly (Opus)", current.AlertSevenDayOpus, palette);
        _alertOpus.Location = new Point(PagePadX, y);
        page.Controls.Add(_alertOpus);

        _alertSonnet = NewCheckBox("Weekly (Sonnet)", current.AlertSevenDaySonnet, palette);
        _alertSonnet.Location = new Point(col2X, y);
        page.Controls.Add(_alertSonnet);
        y += _alertOpus.Height + RowGap;

        page.Controls.Add(NewFieldLabel("Popup placement", palette, y));
        _placement = NewCombo(palette, 130);
        _placement.Location = new Point(FieldX, y);
        _placement.Items.Add("Near tray");
        _placement.Items.Add("Centered");
        _placement.SelectedIndex = current.PopupPlacement == PopupPlacement.Centered ? 1 : 0;
        page.Controls.Add(_placement);
        y += _placement.Height + RowGap;

        // Label goes on its own line above the field (like Backup's "Remote
        // URL") rather than beside it at FieldX - this label is long enough
        // that side-by-side would run into the NumericUpDown.
        var autoDismissLabel = NewSectionLabel("Auto-dismiss popups after (seconds, 0 = never)", palette, y);
        page.Controls.Add(autoDismissLabel);
        y += autoDismissLabel.PreferredHeight + 2;

        _autoDismissInput = NewNumeric(palette, 0, 300, current.PopupAutoDismissSeconds);
        _autoDismissInput.Location = new Point(PagePadX, y);
        page.Controls.Add(_autoDismissInput);
        y += _autoDismissInput.Height + 2;

        var autoDismissHint = NewSubtleLabel(
            "Centered popups (including every 100% popup) always wait for you to dismiss them.",
            palette, DialogWidth - PagePadX * 2);
        autoDismissHint.Location = new Point(PagePadX, y);
        page.Controls.Add(autoDismissHint);
        y += autoDismissHint.PreferredHeight + RowGap;

        // S12 part B: same "label on its own line above the field" layout as
        // the auto-dismiss row just above it - long enough that side-by-side
        // would run into the NumericUpDown.
        var repeatLabel = NewSectionLabel("Re-notify every (minutes, 0 = only once until reset)", palette, y);
        page.Controls.Add(repeatLabel);
        y += repeatLabel.PreferredHeight + 2;

        _alertRepeatInput = NewNumeric(palette, 0, 1440, current.AlertRepeatMinutes);
        _alertRepeatInput.Location = new Point(PagePadX, y);
        page.Controls.Add(_alertRepeatInput);
        y += _alertRepeatInput.Height + RowGap;

        return (page, y + 10);
    }

    /// <summary>
    /// Destination selector, Frequency/Time, action buttons - all shared,
    /// visible regardless of which destination is selected. The GitHub and
    /// Drive connection fields (enable checkbox, remote URL/branch or rclone
    /// remote) plus that destination's own Include/Exclude live in two
    /// separate blocks (see BuildGithubBlock / BuildDriveBlock) that are
    /// both built and added to the page up front, but only one is ever
    /// Visible at a time - see OnBackupDestinationChanged. Fix round 1: this
    /// used to show BOTH destinations' connection fields simultaneously with
    /// only the Include/Exclude boxes switching, which made the Backup tab
    /// tall enough (705px) to run off the bottom of a 1366x768 display at
    /// 100% DPI. Showing only one destination's fields at a time both fixes
    /// the height and makes "which destination am I editing" unambiguous
    /// without any extra dynamic labeling - the visible block IS the answer.
    ///
    /// S16: the selector itself (_backupDestination) went from two nested
    /// dropdowns ("Editing settings for": GitHub/Drive, then, only once
    /// "Drive" was picked, a second "Transport" dropdown) to one flat,
    /// named-destination list - see BackupDestinationOptions. The word
    /// "OneDrive" (or "NAS") now appears directly in the list the user
    /// actually picks from, which is the whole point of this change: a user
    /// asked for OneDrive/NAS backup could not find either word anywhere in
    /// Settings before this, because both were hidden a level down inside a
    /// generic "Drive" destination's own transport choice.
    ///
    /// Only called when BackupTaskManager.WorkerAvailable() - current values
    /// are loaded from _backupConfigPath (BackupConfig.DefaultPath() for
    /// every real caller - see that field's own doc comment).
    /// </summary>
    private (Panel Page, int Height) BuildBackupPage(Palette palette)
    {
        var config = BackupConfig.Load(_backupConfigPath);
        var page = new Panel { Dock = DockStyle.Fill, BackColor = palette.Back, Visible = false };
        var y = PageTopY;
        var fullWidth = DialogWidth - PagePadX * 2;
        var rightEdgeX = PagePadX + fullWidth - InfoButtonSize;

        // S7/S8: seed the Advanced dialog's backing fields from the loaded
        // config up front - BuildBackupPage runs once per SettingsForm, so
        // this is the one place "what the Advanced dialog should show the
        // first time it opens" is read from disk. OnOpenAdvancedDialog
        // reads/writes these same fields on every subsequent open/close.
        _scheduleStartWhenAvailable = config.Schedule.StartWhenAvailable;
        _scheduleRunOnlyIfNetworkAvailable = config.Schedule.RunOnlyIfNetworkAvailable;
        _scheduleDisallowStartIfOnBatteries = config.Schedule.DisallowStartIfOnBatteries;
        _scheduleStopIfGoingOnBatteries = config.Schedule.StopIfGoingOnBatteries;
        _scheduleRestartOnFailure = config.Schedule.RestartOnFailure;
        _scheduleRestartIntervalMinutes = config.Schedule.RestartIntervalMinutes;
        _scheduleRestartCount = config.Schedule.RestartCount;
        _scheduleBackupStaleAfterDays = config.Schedule.BackupStaleAfterDays;
        _driveKeepLastCount = config.Drive.KeepLastCount;
        _driveDeleteOlderThanDays = config.Drive.DeleteOlderThanDays;

        // Shared by every (i) button below - manual Show() calls, not
        // hover-triggered, so the button controls when it appears; it never
        // steals focus (a ToolTip window is never activatable) and is themed
        // to match the dialog rather than falling back to OS tooltip colors.
        _helpTip = new ToolTip
        {
            BackColor = palette.BarBack,
            ForeColor = palette.Fore,
            ShowAlways = true,
        };

        var helpButton = NewFlatButton("Help", palette);
        helpButton.Location = new Point(PagePadX + fullWidth - helpButton.Width, y);
        helpButton.Click += (_, _) =>
        {
            using var dlg = new BackupHelpDialog(palette);
            dlg.ShowDialog(this);
        };
        page.Controls.Add(helpButton);

        // S7/S8: schedule-robustness and Drive-retention settings do not fit
        // as plain rows within the Backup tab's display budget (see the
        // design spec's layout constraint) - a single button placed inline
        // beside the existing Help button (same NewFlatButton type, so it
        // adds no extra row height - unlike placing it beside the shorter
        // destination combo box, which was measured to push the tab's
        // height to 686px, a single pixel under the 687px budget) opens
        // BackupAdvancedDialog instead of adding any new row.
        var advancedButton = NewFlatButton("Advanced...", palette);
        advancedButton.Location = new Point(helpButton.Left - 8 - advancedButton.Width, y);
        advancedButton.Click += (_, _) => OnOpenAdvancedDialog(palette);
        page.Controls.Add(advancedButton);
        y += helpButton.Height + RowGap;

        var selectorLabel = NewSectionLabel("Back up to", palette, y);
        page.Controls.Add(selectorLabel);
        y += selectorLabel.PreferredHeight + 2;

        // S16: one named-destination list replaces the old two nested
        // dropdowns (an "Editing settings for" GitHub/Drive picker, with a
        // second "Transport" dropdown only shown once "Drive" was picked) -
        // see BackupDestinationOptions and InitialDestinationIndex. Falls
        // back to GitHub (index 0) when neither destination is enabled,
        // exactly the same tie-break the old selector used.
        var initialIndex = InitialDestinationIndex(config);

        _backupDestination = NewCombo(palette, 250);
        _backupDestination.Location = new Point(PagePadX, y);
        foreach (var option in BackupDestinationOptions)
            _backupDestination.Items.Add(option.Label);
        _backupDestination.SelectedIndex = initialIndex;
        page.Controls.Add(_backupDestination);
        y += _backupDestination.Height;

        // Say the one-at-a-time rule plainly, right where the choice is made -
        // GitHub plus exactly one cloud/NAS destination can be active; this
        // is not a bug, but it must never be discovered by surprise. Kept to
        // one short line (see the design note on this dialog's tight height
        // budget) - the full rationale lives in the Help guide.
        var oneAtATimeNote = NewSubtleLabel(
            "GitHub + one cloud/NAS destination at a time; picking another replaces it.",
            palette, fullWidth);
        oneAtATimeNote.Location = new Point(PagePadX, y);
        page.Controls.Add(oneAtATimeNote);
        y += oneAtATimeNote.PreferredHeight;

        var initialOption = BackupDestinationOptions[initialIndex];
        _backupDriveTransportSelection = config.Drive.Transport;
        _backupDriveSyncProviderSelection = config.Drive.SyncProvider;

        var (githubBlock, githubBlockHeight) = BuildGithubBlock(config.Github, config.SourceRoot, palette, fullWidth, rightEdgeX);
        var (driveBlock, driveBlockHeight) = BuildDriveBlock(config.Drive, config.SourceRoot, palette, fullWidth, rightEdgeX);
        githubBlock.Location = new Point(0, y);
        driveBlock.Location = new Point(0, y);
        githubBlock.Visible = initialOption.IsGithub;
        driveBlock.Visible = !initialOption.IsGithub;
        // Both blocks are added regardless of the selector's starting value -
        // only Visible toggles thereafter - so every control inside both
        // (including the ones not currently shown) is fully constructed and
        // reachable by OnSaveBackupSchedule the whole time the dialog is
        // open, not just while its block happens to be on screen.
        page.Controls.Add(driveBlock);
        page.Controls.Add(githubBlock);
        _backupGithubBlock = githubBlock;
        _backupDriveBlock = driveBlock;
        _backupDestination.SelectedIndexChanged += (_, _) => OnBackupDestinationChanged(palette);
        y += Math.Max(githubBlockHeight, driveBlockHeight) + RowGap;

        page.Controls.Add(NewFieldLabel("Frequency", palette, y));
        _backupFrequency = NewCombo(palette, 130);
        _backupFrequency.Location = new Point(FieldX, y);
        foreach (var freq in BackupFrequencies)
            _backupFrequency.Items.Add(freq.Display);
        var freqIndex = Array.FindIndex(BackupFrequencies,
            f => f.Value == config.Schedule.Frequency.ToLowerInvariant());
        _backupFrequency.SelectedIndex = Math.Max(0, freqIndex);
        page.Controls.Add(_backupFrequency);
        y += _backupFrequency.Height + RowGap;

        page.Controls.Add(NewFieldLabel("Time (24h HH:mm)", palette, y));
        _backupTime = NewTextBox(config.Schedule.Time, palette, 80);
        _backupTime.Location = new Point(FieldX, y);
        page.Controls.Add(_backupTime);
        y += _backupTime.Height + RowGap;

        var runNowButton = NewFlatButton("Back up now", palette);
        var saveScheduleButton = NewFlatButton("Save and register schedule", palette);
        // S9b: "Restore..." placed inline on this same row, to the left of
        // "Save and register schedule" - same NewFlatButton control as its
        // two neighbours, so (per BuildBackupPage's own layout history for
        // Help/Advanced above) it adds zero extra row height: the row's
        // height is already governed by the tallest of the buttons on it,
        // and all three are the same control type. A new row was not an
        // option - see SettingsFormHeightStaysWithinTheDisplayBudget and the
        // design spec's layout constraint.
        var restoreButton = NewFlatButton("Restore...", palette);
        var runNowX = PagePadX + fullWidth - runNowButton.Width;
        var saveX = runNowX - 8 - saveScheduleButton.Width;
        var restoreX = saveX - 8 - restoreButton.Width;
        runNowButton.Location = new Point(runNowX, y);
        saveScheduleButton.Location = new Point(saveX, y);
        restoreButton.Location = new Point(restoreX, y);
        runNowButton.Click += async (_, _) => await RunBackupNowAsync();
        saveScheduleButton.Click += OnSaveBackupSchedule;
        restoreButton.Click += (_, _) => OnOpenRestoreDialog(palette);
        page.Controls.Add(saveScheduleButton);
        page.Controls.Add(runNowButton);
        page.Controls.Add(restoreButton);
        y += Math.Max(runNowButton.Height, Math.Max(saveScheduleButton.Height, restoreButton.Height)) + RowGap;

        return (page, y + 10);
    }

    /// <summary>
    /// GitHub's connection fields (enable, remote URL, privacy caption,
    /// branch) plus GitHub's own Include/Exclude. A plain Panel (not
    /// Dock = Fill) sized to exactly its own content, positioned by the
    /// caller (BuildBackupPage) at the shared Y where the destination
    /// blocks begin - its children use the same PagePadX/FieldX offsets
    /// every other page uses, since the panel's own Location.X is 0 and
    /// therefore does not shift their absolute position.
    /// </summary>
    private (Panel Block, int Height) BuildGithubBlock(GitTarget target, string sourceRoot, Palette palette, int fullWidth, int rightEdgeX)
    {
        var block = new Panel { BackColor = palette.Back };
        var y = 0;

        _backupGithubEnabled = NewCheckBox("Back up to a GitHub repo", target.Enabled, palette);
        _backupGithubEnabled.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupGithubEnabled);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.GithubEnabled);
        y += _backupGithubEnabled.Height + RowGap;

        var urlLabel = NewSectionLabel("Remote URL", palette, y);
        block.Controls.Add(urlLabel);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.RemoteUrl);
        y += urlLabel.PreferredHeight + 2;

        _backupGithubUrl = NewTextBox(target.RemoteUrl, palette, fullWidth);
        _backupGithubUrl.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupGithubUrl);
        y += _backupGithubUrl.Height + RowGap;

        // I2: the security model's non-negotiable guardrail - ClaudeCounter
        // has no way to call the GitHub API and check a repo's visibility, so
        // it cannot enforce privacy. The one thing it can do is make sure the
        // user is not left assuming it was checked for them.
        var privacyCaption = NewSubtleLabel(
            "This repo must be private. ClaudeCounter cannot verify that automatically.",
            palette, fullWidth);
        privacyCaption.Location = new Point(PagePadX, y);
        block.Controls.Add(privacyCaption);
        y += privacyCaption.PreferredHeight + RowGap;

        block.Controls.Add(NewFieldLabel("Branch", palette, y));
        _backupGithubBranch = NewTextBox(target.Branch, palette, 130);
        _backupGithubBranch.Location = new Point(FieldX, y);
        block.Controls.Add(_backupGithubBranch);
        AddInfoButton(block, palette, FieldX + 130 + 8, y + 3, BackupHelpText.Branch);
        y += _backupGithubBranch.Height + RowGap;

        var includeLabel = NewSectionLabel("Include (one pattern per line)", palette, y);
        block.Controls.Add(includeLabel);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.Include);
        var chooseGithubButton = AddChooseFilesButton(
            block, palette, "GitHub", sourceRoot, () => _backupGithubInclude!, rightEdgeX - InfoButtonSize - 8, y - 3);
        y += Math.Max(includeLabel.PreferredHeight, chooseGithubButton.Height) + 2;
        _backupGithubInclude = NewTextBox(string.Join(Environment.NewLine, target.Include), palette, fullWidth, multiline: true, height: 55);
        _backupGithubInclude.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupGithubInclude);
        y += _backupGithubInclude.Height + RowGap;

        var excludeLabel = NewSectionLabel("Exclude (one pattern per line)", palette, y);
        block.Controls.Add(excludeLabel);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.Exclude);
        y += excludeLabel.PreferredHeight + 2;
        _backupGithubExclude = NewTextBox(string.Join(Environment.NewLine, target.Exclude), palette, fullWidth, multiline: true, height: 55);
        _backupGithubExclude.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupGithubExclude);
        y += _backupGithubExclude.Height + RowGap;

        block.Size = new Size(fullWidth + PagePadX * 2, y);
        return (block, y);
    }

    /// <summary>
    /// Drive's connection fields (enable checkbox, then EITHER the
    /// sync-folder path row OR the rclone-remote row depending on the
    /// top-level "Back up to" selection) plus Drive's own Include/Exclude.
    /// See BuildGithubBlock's doc comment for the general layout reasoning.
    ///
    /// S14b: the transport row-swap (BuildSyncFolderRow / BuildRcloneRow) is
    /// a nested instance of the exact same "both built, only one Visible"
    /// pattern BuildBackupPage already uses for
    /// _backupGithubBlock/_backupDriveBlock - both row panels are always
    /// fully constructed (so OnSaveBackupSchedule can read either one's
    /// controls regardless of which is on screen), and the space reserved
    /// below them is Math.Max(folderRowHeight, rcloneRowHeight), so swapping
    /// costs no more dialog height than whichever row happens to be taller.
    ///
    /// S16: what used to toggle that swap - this block's own "Transport"
    /// ComboBox - is gone. The row-swap is now driven by
    /// OnBackupDestinationChanged, reacting to the top-level "Back up to"
    /// selector in BuildBackupPage instead - one flat selector instead of
    /// two nested ones, but the same swap mechanics underneath.
    /// </summary>
    private (Panel Block, int Height) BuildDriveBlock(DriveTarget target, string sourceRoot, Palette palette, int fullWidth, int rightEdgeX)
    {
        var block = new Panel { BackColor = palette.Back };
        var y = 0;

        // S16: "Back up to this destination", not "Back up to Drive" - the
        // "Back up to" selector above now names the destination directly
        // (Google Drive / OneDrive / Dropbox / NAS / rclone remote), so a
        // second, generic "Drive" label here would just be redundant noise
        // next to it rather than adding information.
        _backupDriveEnabled = NewCheckBox("Back up to this destination", target.Enabled, palette);
        _backupDriveEnabled.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupDriveEnabled);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.DriveEnabled);
        y += _backupDriveEnabled.Height + RowGap;

        // S16: no more "Transport" combo here - the top-level "Back up to"
        // selector (BuildBackupPage) now IS the transport choice, so this
        // block only needs to show whichever row that selection implies.
        // Initial visibility still comes from the loaded target.Transport
        // (matches _backupDriveTransportSelection's own initial value, set
        // by BuildBackupPage from the same config); OnBackupDestinationChanged
        // takes over from there whenever the user changes the selector.
        var (folderRow, folderRowHeight) = BuildSyncFolderRow(target, palette, fullWidth, rightEdgeX);
        var (rcloneRow, rcloneRowHeight) = BuildRcloneRow(target, palette);
        var showSyncFolder = target.Transport == DriveTransport.SyncFolder;
        folderRow.Location = new Point(0, y);
        rcloneRow.Location = new Point(0, y);
        folderRow.Visible = showSyncFolder;
        rcloneRow.Visible = !showSyncFolder;
        // Both rows are added regardless of the starting transport - only
        // Visible toggles thereafter - mirroring BuildBackupPage's own
        // comment on githubBlock/driveBlock.
        block.Controls.Add(rcloneRow);
        block.Controls.Add(folderRow);
        _backupDriveFolderRow = folderRow;
        _backupDriveRcloneRow = rcloneRow;
        y += Math.Max(folderRowHeight, rcloneRowHeight) + RowGap;

        var includeLabel = NewSectionLabel("Include (one pattern per line)", palette, y);
        block.Controls.Add(includeLabel);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.Include);
        // "Drive", not the currently-picked destination's own name - this is
        // only a picker dialog title, captured once in a closure at
        // construction time, so it cannot track a later change to the "Back
        // up to" selector above either.
        var chooseDriveButton = AddChooseFilesButton(
            block, palette, "Drive", sourceRoot, () => _backupDriveInclude!, rightEdgeX - InfoButtonSize - 8, y - 3);
        y += Math.Max(includeLabel.PreferredHeight, chooseDriveButton.Height) + 2;
        _backupDriveInclude = NewTextBox(string.Join(Environment.NewLine, target.Include), palette, fullWidth, multiline: true, height: 55);
        _backupDriveInclude.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupDriveInclude);
        y += _backupDriveInclude.Height + RowGap;

        var excludeLabel = NewSectionLabel("Exclude (one pattern per line)", palette, y);
        block.Controls.Add(excludeLabel);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.Exclude);
        y += excludeLabel.PreferredHeight + 2;
        _backupDriveExclude = NewTextBox(string.Join(Environment.NewLine, target.Exclude), palette, fullWidth, multiline: true, height: 55);
        _backupDriveExclude.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupDriveExclude);
        y += _backupDriveExclude.Height + RowGap;

        block.Size = new Size(fullWidth + PagePadX * 2, y);
        return (block, y);
    }

    /// <summary>
    /// The sync-folder transport's own row: a "Sync folder path" label with
    /// Browse... and Detect... inline on the SAME row (mirrors
    /// AddChooseFilesButton's "button inline on the label row, not a row of
    /// its own" trick - see that method's doc comment on why the Backup
    /// tab's height budget makes this matter), then the path textbox on the
    /// row below. A plain Panel positioned by the caller (BuildDriveBlock)
    /// at 0,0 - like every other block/row Panel in this file, its
    /// children's absolute X coordinates (PagePadX, FieldX, ...) are
    /// unaffected by the panel's own Location.
    /// </summary>
    private (Panel Row, int Height) BuildSyncFolderRow(DriveTarget target, Palette palette, int fullWidth, int rightEdgeX)
    {
        var row = new Panel { BackColor = palette.Back };
        var y = 0;

        var label = NewSectionLabel("Sync folder path", palette, y);
        row.Controls.Add(label);
        AddInfoButton(row, palette, rightEdgeX, y, BackupHelpText.SyncFolder);

        var detectButton = NewFlatButton("Detect...", palette);
        var browseButton = NewFlatButton("Browse...", palette);
        detectButton.Location = new Point(rightEdgeX - InfoButtonSize - 8 - detectButton.Width, y - 3);
        browseButton.Location = new Point(detectButton.Left - 8 - browseButton.Width, y - 3);
        detectButton.Click += (_, _) => OnDetectSyncFolder(palette);
        browseButton.Click += (_, _) => OnBrowseSyncFolder();
        row.Controls.Add(detectButton);
        row.Controls.Add(browseButton);
        y += Math.Max(label.PreferredHeight, Math.Max(detectButton.Height, browseButton.Height)) + 2;

        _backupDriveFolderPath = NewTextBox(target.FolderPath, palette, fullWidth);
        _backupDriveFolderPath.Location = new Point(PagePadX, y);
        row.Controls.Add(_backupDriveFolderPath);
        y += _backupDriveFolderPath.Height + RowGap;

        row.Size = new Size(fullWidth + PagePadX * 2, y);
        return (row, y);
    }

    /// <summary>
    /// The rclone transport's own row: exactly the pre-S14b "Rclone remote"
    /// label + textbox + info button, just extracted into its own Panel so
    /// it can swap visibility against BuildSyncFolderRow's panel instead of
    /// always being on screen.
    /// </summary>
    private (Panel Row, int Height) BuildRcloneRow(DriveTarget target, Palette palette)
    {
        var row = new Panel { BackColor = palette.Back };
        var y = 0;

        row.Controls.Add(NewFieldLabel("Rclone remote", palette, y));
        _backupDriveRemote = NewTextBox(target.RcloneRemote, palette, 130);
        _backupDriveRemote.Location = new Point(FieldX, y);
        row.Controls.Add(_backupDriveRemote);
        AddInfoButton(row, palette, FieldX + 130 + 8, y + 3, BackupHelpText.RcloneRemote);
        y += _backupDriveRemote.Height + RowGap;

        row.Size = new Size(FieldX + 130 + 8 + InfoButtonSize, y);
        return (row, y);
    }

    /// <summary>
    /// Opens a standard FolderBrowserDialog seeded from the textbox's
    /// current (possibly unsaved) text when that text is itself an existing
    /// directory, matching AddChooseFilesButton's "seed from what is on
    /// screen, not from disk" rule. On Cancel the textbox is untouched.
    /// </summary>
    private void OnBrowseSyncFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder your sync client (Google Drive, OneDrive, Dropbox) or NAS already watches.",
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(_backupDriveFolderPath!.Text.Trim()))
            dialog.SelectedPath = _backupDriveFolderPath.Text.Trim();

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _backupDriveFolderPath.Text = dialog.SelectedPath;
    }

    /// <summary>
    /// S14b: the biggest usability win of the sync-folder transport - runs
    /// SyncFolderScanner.Detect() (never throws - see its own doc comment)
    /// and lets the user pick from whatever it found via
    /// SyncFolderDetectDialog, rather than making them go find the path
    /// themselves. On Cancel, or when the dialog is dismissed without a
    /// selection, the textbox is left untouched.
    ///
    /// S16: scoped to whichever destination is currently selected (via
    /// SyncFolderScanner.FilterByProvider) rather than offering every
    /// candidate found on the machine - picking "OneDrive" and then clicking
    /// Detect... again (e.g. after signing into a sync client that was not
    /// set up yet when Settings was first opened) should only ever offer
    /// OneDrive candidates, not a Dropbox or NAS one the user did not ask
    /// for here. Retained as a standalone button alongside the automatic
    /// detect-on-select (see OnBackupDestinationChanged/AutoDetectForProvider)
    /// rather than folded away entirely, specifically for this re-detect
    /// case - the automatic version only runs once, at the moment the
    /// selector changes.
    /// </summary>
    private void OnDetectSyncFolder(Palette palette)
    {
        var provider = BackupDestinationOptions[_backupDestination!.SelectedIndex].Provider;
        var candidates = SyncFolderScanner.FilterByProvider(SyncFolderScanner.Detect(), provider);
        using var dialog = new SyncFolderDetectDialog(palette, candidates);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedPath is { } path)
            _backupDriveFolderPath!.Text = path;
    }

    /// <summary>
    /// S16: fires whenever the user changes the "Back up to" selector (never
    /// during construction - BuildBackupPage sets SelectedIndex before
    /// wiring this handler, exactly like the old two-dropdown version did,
    /// so opening Settings on an already-configured destination never
    /// re-triggers detection or clears anything). Swaps which block/row is
    /// visible, and - only when the newly-picked option is a sync-folder
    /// destination - updates the two "what should be saved for Drive"
    /// memory fields and runs auto-detection for it. Picking GitHub leaves
    /// _backupDriveTransportSelection/_backupDriveSyncProviderSelection (and
    /// the folder-path/rclone-remote textboxes) exactly as they were, so
    /// switching to GitHub and back changes nothing about the Drive slot.
    /// </summary>
    private void OnBackupDestinationChanged(Palette palette)
    {
        var option = BackupDestinationOptions[_backupDestination!.SelectedIndex];
        _backupGithubBlock!.Visible = option.IsGithub;
        _backupDriveBlock!.Visible = !option.IsGithub;
        if (option.IsGithub)
            return;

        _backupDriveTransportSelection = option.Transport;
        var showSyncFolder = option.Transport == DriveTransport.SyncFolder;
        _backupDriveFolderRow!.Visible = showSyncFolder;
        _backupDriveRcloneRow!.Visible = !showSyncFolder;

        if (!showSyncFolder)
            return; // rclone remote (advanced): no auto-detection for this one.

        _backupDriveSyncProviderSelection = option.Provider;
        AutoDetectForProvider(option.Provider, palette);
    }

    /// <summary>
    /// S16: the whole point of naming the destinations - selecting OneDrive/
    /// Google Drive/Dropbox/NAS should do the path-finding work for the user
    /// instead of making them go find it themselves (mirrors Detect...'s own
    /// reasoning above, just triggered by the selection itself). Exactly one
    /// match is applied directly; zero matches clears the box rather than
    /// leaving a PREVIOUS destination's now-mismatched path sitting there -
    /// never invents a path either; more than one match opens the same
    /// SyncFolderDetectDialog Detect... uses, scoped to this provider, so
    /// the user picks rather than this silently guessing - on Cancel the box
    /// is left exactly as it was, the same as every other
    /// Cancel-leaves-it-alone control in this dialog.
    /// </summary>
    private void AutoDetectForProvider(SyncProvider provider, Palette palette)
    {
        var candidates = SyncFolderScanner.FilterByProvider(SyncFolderScanner.Detect(), provider);
        switch (candidates.Count)
        {
            case 0:
                _backupDriveFolderPath!.Text = "";
                break;
            case 1:
                _backupDriveFolderPath!.Text = candidates[0].Path;
                break;
            default:
                using (var dialog = new SyncFolderDetectDialog(palette, candidates))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedPath is { } path)
                        _backupDriveFolderPath!.Text = path;
                }
                break;
        }
    }

    /// <summary>
    /// S7/S8: opens BackupAdvancedDialog seeded from the current in-memory
    /// values of the schedule-robustness and Drive-retention fields (which
    /// themselves start out seeded from disk in BuildBackupPage), and on
    /// DialogResult.OK writes the dialog's result back onto those same
    /// fields. On Cancel (or closing via Esc/the X button), nothing changes -
    /// mirrors AddChooseFilesButton's Cancel-leaves-the-box-untouched
    /// behaviour for the same reason. Nothing is persisted here; like every
    /// other Backup tab field, that only happens when "Save and register
    /// schedule" is clicked (see OnSaveBackupSchedule).
    /// </summary>
    private void OnOpenAdvancedDialog(Palette palette)
    {
        var schedule = new ScheduleConfig
        {
            StartWhenAvailable = _scheduleStartWhenAvailable,
            RunOnlyIfNetworkAvailable = _scheduleRunOnlyIfNetworkAvailable,
            DisallowStartIfOnBatteries = _scheduleDisallowStartIfOnBatteries,
            StopIfGoingOnBatteries = _scheduleStopIfGoingOnBatteries,
            RestartOnFailure = _scheduleRestartOnFailure,
            RestartIntervalMinutes = _scheduleRestartIntervalMinutes,
            RestartCount = _scheduleRestartCount,
            BackupStaleAfterDays = _scheduleBackupStaleAfterDays,
        };
        var drive = new DriveTarget
        {
            KeepLastCount = _driveKeepLastCount,
            DeleteOlderThanDays = _driveDeleteOlderThanDays,
        };

        using var dialog = new BackupAdvancedDialog(palette, schedule, drive);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _scheduleStartWhenAvailable = dialog.StartWhenAvailable;
        _scheduleRunOnlyIfNetworkAvailable = dialog.RunOnlyIfNetworkAvailable;
        _scheduleDisallowStartIfOnBatteries = dialog.DisallowStartIfOnBatteries;
        _scheduleStopIfGoingOnBatteries = dialog.StopIfGoingOnBatteries;
        _scheduleRestartOnFailure = dialog.RestartOnFailure;
        _scheduleRestartIntervalMinutes = dialog.RestartIntervalMinutes;
        _scheduleRestartCount = dialog.RestartCount;
        _scheduleBackupStaleAfterDays = dialog.BackupStaleAfterDays;
        _driveKeepLastCount = dialog.KeepLastCount;
        _driveDeleteOlderThanDays = dialog.DeleteOlderThanDays;
    }

    /// <summary>
    /// S9b: opens RestoreDialog against whatever is currently SAVED to
    /// backup.json - not the possibly-unsaved edits sitting in this form's
    /// own textboxes right now. Mirrors RunBackupNowAsync, which reloads the
    /// same way for the same reason: "Back up now" launches ClaudeBackup.exe,
    /// which itself only ever reads backup.json from disk, so restore
    /// operating on the same saved snapshot of config keeps both actions
    /// consistent with each other. Refuses to even open the dialog when
    /// neither destination is enabled, rather than leaving RestoreDialog to
    /// show an empty "no destination" state - a quick, purely informational
    /// MessageBox reads better than an empty dialog for the common case
    /// (nothing configured yet) this guards against.
    /// </summary>
    private void OnOpenRestoreDialog(Palette palette)
    {
        var config = BackupConfig.Load(_backupConfigPath);
        if (!config.Github.Enabled && !config.Drive.Enabled)
        {
            // Transport-neutral - mirrors RestoreDialog's identical "neither
            // destination enabled" message (see its own comment there).
            MessageBox.Show(this,
                "No backup destination is enabled. Enable and configure GitHub or Drive backup first.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new RestoreDialog(palette, config, new ProcessRunner());
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// Adds a small themed info button at (x, y) that shows <paramref
    /// name="text"/> in the shared _helpTip on click. Only called from
    /// BuildBackupPage (directly) and BuildGithubBlock/BuildDriveBlock
    /// (for a block Panel), both of which run after BuildBackupPage has
    /// created _helpTip.
    /// </summary>
    private void AddInfoButton(Panel page, Palette palette, int x, int y, string text)
    {
        var button = new InfoButton(palette) { Location = new Point(x, y) };
        button.Click += (_, _) => _helpTip!.Show(text, button, button.Width + 4, 0, 15000);
        page.Controls.Add(button);
    }

    /// <summary>
    /// S6: adds a "Choose files..." button, right-aligned to end at
    /// <paramref name="rightX"/>, that opens BackupPickerDialog scoped to
    /// ONE destination's own Include list - GitHub and Drive each get their
    /// own call, with their own <paramref name="destinationName"/> (shown in
    /// the picker's title, per the design spec) and their own Include
    /// textbox. Seeded from the textbox's CURRENT (possibly unsaved) text,
    /// not from disk, so a picker opened after editing the box by hand
    /// starts from what is actually on screen - the same "read the live
    /// control" rule OnSaveBackupSchedule already follows. On OK, the box is
    /// overwritten with the dialog's generated pattern list (which itself
    /// preserves anything the tree could not represent - see
    /// BackupTreeModel); on Cancel, the box is untouched.
    ///
    /// Placed inline on the existing Include label's row (see
    /// BuildGithubBlock/BuildDriveBlock) rather than on a new row of its
    /// own - the Backup tab's page height budget is tight enough that this
    /// note matters (see BuildBackupPage's own doc comment history).
    /// </summary>
    private Button AddChooseFilesButton(
        Panel block, Palette palette, string destinationName, string sourceRoot,
        Func<TextBox> includeBoxAccessor, int rightX, int y)
    {
        var button = NewFlatButton("Choose files...", palette);
        button.Location = new Point(rightX - button.Width, y);
        button.Click += (_, _) =>
        {
            var includeBox = includeBoxAccessor();
            var current = SplitLines(includeBox.Text);
            using var dialog = new BackupPickerDialog(palette, destinationName, sourceRoot, current);
            if (dialog.ShowDialog(this) == DialogResult.OK)
                includeBox.Text = string.Join(Environment.NewLine, dialog.Include);
        };
        block.Controls.Add(button);
        return button;
    }

    private static Label NewFieldLabel(string text, Palette palette, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Font = BaseFont,
        ForeColor = palette.Fore,
        BackColor = Color.Transparent,
        Location = new Point(PagePadX, y + LabelYOffset),
    };

    private static Label NewSectionLabel(string text, Palette palette, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Font = BaseFont,
        ForeColor = palette.Fore,
        BackColor = Color.Transparent,
        Location = new Point(PagePadX, y),
    };

    private static Label NewSubtleLabel(string text, Palette palette, int maxWidth) => new()
    {
        Text = text,
        AutoSize = true,
        Font = BaseFont,
        MaximumSize = new Size(maxWidth, 0),
        ForeColor = palette.SubtleFore,
        BackColor = Color.Transparent,
    };

    private static ComboBox NewCombo(Palette palette, int width) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        Font = BaseFont,
        Width = width,
        BackColor = palette.Back,
        ForeColor = palette.Fore,
    };

    private static NumericUpDown NewNumeric(Palette palette, int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        Width = 70,
        Font = BaseFont,
        BackColor = palette.Back,
        ForeColor = palette.Fore,
        BorderStyle = BorderStyle.FixedSingle,
    };

    // A stock CheckBox with FlatStyle.Flat still renders its indicator box
    // using colors it cannot be told about - on the dark palette that draws a
    // dark box with no visible border and no visible check glyph, so ticked
    // and unticked look identical. ThemedCheckBox (below) replaces only the
    // painting; Checked, click-to-toggle, Space-to-toggle and AutoSize all
    // still come from the base CheckBox class.
    private static CheckBox NewCheckBox(string text, bool @checked, Palette palette) => new ThemedCheckBox(palette)
    {
        Text = text,
        AutoSize = true,
        Checked = @checked,
        Font = BaseFont,
    };

    private static TextBox NewTextBox(string text, Palette palette, int width, bool multiline = false, int height = 23)
    {
        var box = new TextBox
        {
            Text = text,
            Width = width,
            Multiline = multiline,
            Font = BaseFont,
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

    private static Button NewFlatButton(string text, Palette palette)
    {
        var button = new Button
        {
            Text = text,
            Font = BaseFont,
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
            Font = BaseFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = palette.BarBack,
            ForeColor = palette.Fore,
            Size = new Size(84, 28),
        };
        button.FlatAppearance.BorderColor = palette.Border;
        return button;
    }

    /// <summary>
    /// I1: report the worker's actual exit code instead of firing it and
    /// forgetting - a failing backup previously looked identical to a
    /// successful one. Awaiting does not block the UI thread: RunNowAsync
    /// awaits WaitForExitAsync, which yields back to the message loop.
    /// </summary>
    private async Task RunBackupNowAsync()
    {
        var exitCode = await BackupTaskManager.RunNowAsync();
        var message = BackupTaskManager.ResultMessage(exitCode);
        MessageBox.Show(this, message, "ClaudeCounter", MessageBoxButtons.OK,
            exitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void OnSaveBackupSchedule(object? sender, EventArgs e)
    {
        var time = _backupTime!.Text.Trim();
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            MessageBox.Show(this, "Time must be a 24-hour value in HH:mm format, e.g. 09:00.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var remoteUrl = _backupGithubUrl!.Text.Trim();
        if (HasEmbeddedCredential(remoteUrl))
        {
            MessageBox.Show(this,
                "Remote URL must not embed a credential (e.g. https://user:token@host/...). " +
                "backup.json is never allowed to contain a secret - set up Git Credential " +
                "Manager (or an SSH key) for this remote instead.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var rcloneRemote = _backupDriveRemote!.Text.Trim();
        if (HasLeadingDash(rcloneRemote))
        {
            MessageBox.Show(this,
                "Rclone remote must not start with '-' - rclone would parse it as an option " +
                "rather than a remote name.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // S5, fix round 1: GitHub's and Drive's fields are now two genuinely
        // separate blocks of controls (both always constructed, only one
        // ever Visible - see BuildBackupPage/BuildGithubBlock/
        // BuildDriveBlock), so both can be read directly here regardless of
        // which one the user currently has on screen. No flush-from-whichever-
        // is-visible step is needed any more.
        var config = BackupConfig.Load(_backupConfigPath);

        // S14b: validate the sync-folder path the same way SyncFolderBackend
        // itself would at write time - SyncFolderPathValidator.Validate is
        // the exact same rule set (ClaudeCounter.csproj has no
        // ProjectReference to ClaudeBackup.csproj, so it cannot call
        // SyncFolderBackend.ValidateFolderPath directly; both now forward to
        // this one shared implementation - see that class's doc comment).
        // Gated on "Drive enabled and sync-folder transport selected" rather
        // than run unconditionally: a blank rclone remote is likewise never
        // blocked here (BackupRunner catches that at run time instead), and
        // the validator's own blank-path message ("...is enabled but no
        // folder is configured") is only accurate under that same condition.
        //
        // S16: driveTransport/driveProvider now come from
        // _backupDriveTransportSelection/_backupDriveSyncProviderSelection -
        // the "Back up to" selector's own SelectedIndex is not enough on its
        // own here, because it may currently be showing GitHub, and these
        // two fields are exactly what remembers the Drive slot's last
        // selection through that (see their own doc comment).
        var driveTransport = _backupDriveTransportSelection;
        var driveFolderPath = _backupDriveFolderPath!.Text.Trim();
        if (_backupDriveEnabled!.Checked && driveTransport == DriveTransport.SyncFolder)
        {
            var folderError = SyncFolderPathValidator.Validate(driveFolderPath, config.SourceRoot);
            if (folderError is not null)
            {
                MessageBox.Show(this, folderError, "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        config.Github.Enabled = _backupGithubEnabled!.Checked;
        config.Github.RemoteUrl = remoteUrl;
        config.Github.Branch = _backupGithubBranch!.Text.Trim();
        config.Github.Include = SplitLines(_backupGithubInclude!.Text);
        config.Github.Exclude = SplitLines(_backupGithubExclude!.Text);
        config.Drive.Enabled = _backupDriveEnabled.Checked;
        config.Drive.Transport = driveTransport;
        config.Drive.SyncProvider = _backupDriveSyncProviderSelection;
        config.Drive.FolderPath = driveFolderPath;
        config.Drive.RcloneRemote = rcloneRemote;
        config.Drive.Include = SplitLines(_backupDriveInclude!.Text);
        config.Drive.Exclude = SplitLines(_backupDriveExclude!.Text);
        config.Schedule.Frequency = BackupFrequencies[_backupFrequency!.SelectedIndex].Value;
        config.Schedule.Time = time;
        config.Schedule.StartWhenAvailable = _scheduleStartWhenAvailable;
        config.Schedule.RunOnlyIfNetworkAvailable = _scheduleRunOnlyIfNetworkAvailable;
        config.Schedule.DisallowStartIfOnBatteries = _scheduleDisallowStartIfOnBatteries;
        config.Schedule.StopIfGoingOnBatteries = _scheduleStopIfGoingOnBatteries;
        config.Schedule.RestartOnFailure = _scheduleRestartOnFailure;
        config.Schedule.RestartIntervalMinutes = _scheduleRestartIntervalMinutes;
        config.Schedule.RestartCount = _scheduleRestartCount;
        config.Schedule.BackupStaleAfterDays = _scheduleBackupStaleAfterDays;
        config.Drive.KeepLastCount = _driveKeepLastCount;
        config.Drive.DeleteOlderThanDays = _driveDeleteOlderThanDays;

        config.Save(_backupConfigPath);

        // Unticking both destinations and saving must not silently recreate a
        // task that would run a backup nobody asked for anymore.
        var destinationEnabled = config.Github.Enabled || config.Drive.Enabled;
        bool ok;
        string message;
        if (destinationEnabled)
        {
            ok = BackupTaskManager.Register(config.Schedule);
            message = ok
                ? "Backup settings saved and the schedule registered."
                : "Backup settings saved, but registering the schedule failed. See the log for details.";
        }
        else
        {
            var outcome = BackupTaskManager.Unregister();
            ok = outcome != UnregisterOutcome.Failed;
            message = outcome switch
            {
                UnregisterOutcome.Removed =>
                    "Backup settings saved. No destination is enabled, so the schedule was removed.",
                UnregisterOutcome.NotFound =>
                    "Backup settings saved. No destination is enabled; there was no schedule to remove.",
                _ => "Backup settings saved, but removing the existing schedule failed. See the log for details.",
            };
        }

        MessageBox.Show(this, message, "ClaudeCounter", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static List<string> SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // I3: matches a URL scheme followed by a userinfo component
    // (scheme://user[:pass]@...) - the shape a credential-bearing HTTPS
    // remote takes (e.g. "https://ghp_xxx@github.com/org/repo.git"). Does NOT
    // match the SSH shorthand form ("git@github.com:org/repo.git"): that has
    // no "scheme://" prefix at all, and the "git@" there is a fixed username,
    // not a secret. Public static (not requiring a Form instance) so it is
    // directly unit-testable per the project's rule against constructing a
    // Form in a test.
    private static readonly Regex EmbeddedCredentialPattern =
        new(@"^[a-z][a-z0-9+.\-]*://[^/@]*@", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="url"/> carries a userinfo component that
    /// would put a credential into backup.json in plain text - the project's
    /// hard constraint is that no secret is ever written there. The fix for
    /// a user who needs authentication is Git Credential Manager or an SSH
    /// key, not embedding a token in the remote URL.
    /// </summary>
    public static bool HasEmbeddedCredential(string? url) =>
        !string.IsNullOrEmpty(url) && EmbeddedCredentialPattern.IsMatch(url);

    /// <summary>
    /// True when <paramref name="remote"/> starts with '-'. An rclone remote
    /// spec passed on the command line as a bare positional argument is
    /// parsed as an option if it starts with a dash - ArgumentList prevents
    /// shell injection but not this, so it is rejected in the UI instead.
    /// </summary>
    public static bool HasLeadingDash(string? remote) =>
        !string.IsNullOrEmpty(remote) && remote.StartsWith('-');

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        Activate();
        TopMost = false;
    }

    // _helpTip is a Component, not a Control - it is never in the Controls
    // tree, so nothing else disposes it. Mirrors AlertPopupForm's own
    // Dispose override for its dismiss timer.
    protected override void Dispose(bool disposing)
    {
        if (disposing) _helpTip?.Dispose();
        base.Dispose(disposing);
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (_warnInput.Value >= _criticalInput.Value)
        {
            MessageBox.Show(this, "Warn threshold must be lower than the critical threshold.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Apply form values onto a settings object (call after DialogResult.OK).</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.PollIntervalMinutes = AppSettings.IntervalPresets[_intervalCombo.SelectedIndex];
        settings.WarnThreshold = (int)_warnInput.Value;
        settings.CriticalThreshold = (int)_criticalInput.Value;
        settings.AutostartEnabled = _autostartCheck.Checked;
        settings.CheckForUpdates = _updateCheck.Checked;
        settings.WarnAlertsEnabled = _warnAlerts.Checked;
        settings.CriticalAlertsEnabled = _criticalAlerts.Checked;
        settings.MaxedAlertsEnabled = _maxedAlerts.Checked;
        settings.AlertFiveHour = _alertFiveHour.Checked;
        settings.AlertSevenDay = _alertSevenDay.Checked;
        settings.AlertSevenDayOpus = _alertOpus.Checked;
        settings.AlertSevenDaySonnet = _alertSonnet.Checked;
        settings.PopupPlacement = _placement.SelectedIndex == 1
            ? PopupPlacement.Centered : PopupPlacement.NearTray;
        settings.PopupAutoDismissSeconds = (int)_autoDismissInput.Value;
        settings.AlertRepeatMinutes = (int)_alertRepeatInput.Value;
    }

    /// <summary>
    /// A single flat, themed tab button. A plain Control (not a Button) so it
    /// can be fully custom-painted - the active tab is marked with a solid
    /// underline plus a brighter foreground rather than any 3D chrome.
    /// Focusable via Tab, and Enter/Space activates it the same as a click -
    /// without this a keyboard user could never reach Alerts or Backup, which
    /// would be a reach regression versus the old single-page dialog where
    /// every control was plain Tab-order reachable.
    /// </summary>
    private sealed class TabButton : Control
    {
        private readonly Palette _palette;

        public bool IsActive { get; set; }

        public TabButton(string text, Palette palette)
        {
            _palette = palette;
            Text = text;
            BackColor = palette.BarBack;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        }

        // Without claiming Enter and Space as input keys here, Enter would be
        // swallowed by the Form's AcceptButton (OK) before this control ever
        // sees it, and Space is not guaranteed to reach a plain Control's
        // OnKeyDown either - both need to activate the focused tab instead.
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

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var color = IsActive ? _palette.Fore : _palette.SubtleFore;
            TextRenderer.DrawText(e.Graphics, Text, TabFont, ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (IsActive)
            {
                using var brush = new SolidBrush(_palette.Fore);
                e.Graphics.FillRectangle(brush, 0, Height - 3, Width, 3);
            }
            // Distinct from the active-tab underline: this marks keyboard
            // focus, which can land on an inactive tab (e.g. tabbing onto
            // Alerts while General is still the shown page, before Enter/
            // Space is pressed) and must stay visibly different from it.
            if (Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
        }
    }

    /// <summary>
    /// A small themed "(i)" affordance for a single Backup field's help text.
    /// Drawn entirely with GDI+ primitives (an ellipse plus the letter "i" in
    /// the same font already used everywhere else in this dialog) rather than
    /// relying on the Unicode U+24D8 CIRCLED LATIN SMALL LETTER I glyph.
    /// Chosen during implementation, not mandated by any design document: the
    /// glyph's font coverage could not be screenshot-verified at 100/125/150%
    /// DPI without launching the GUI, which this task's own instructions
    /// ruled out, and a hand-drawn circle sidesteps that risk entirely rather
    /// than gambling on it. See
    /// docs/superpowers/specs/2026-08-10-settings-redesign.md's constraints
    /// section for where this fallback is recorded. Click (or Enter/Space
    /// when focused) shows the themed, non-activating ToolTip owned by the
    /// containing SettingsForm.
    /// </summary>
    private sealed class InfoButton : Control
    {
        // Deliberately smaller and bolder than BaseFont, not just BaseFont
        // reused: at BaseFont's 9pt the "i" glyph plus its side bearings
        // does not comfortably fit inside a 16px circle at 100% DPI, let
        // alone 150%.
        private static readonly Font InfoFont = new("Segoe UI", 7.5f, FontStyle.Bold);

        private readonly Palette _palette;

        public InfoButton(Palette palette)
        {
            _palette = palette;
            Size = new Size(InfoButtonSize, InfoButtonSize);
            Font = InfoFont;
            Cursor = Cursors.Hand;
            TabStop = true;
            // SupportsTransparentBackColor must be enabled BEFORE assigning a
            // transparent BackColor, and a plain Control does not opt in by
            // default: Control.set_BackColor throws "Control does not support
            // transparent background colors" otherwise, which crashed the app
            // the moment the Backup tab was built. ThemedCheckBox gets away
            // with the same assignment only because ButtonBase opts in for it.
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

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? _palette.Back);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

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

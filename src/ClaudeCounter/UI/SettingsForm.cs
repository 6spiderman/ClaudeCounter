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
    // below (BackupConfig, ScheduleConfig, ...) live in ClaudeCounter.Core
    // instead. Do not add a reference to ClaudeBackup.csproj here; it drags the
    // worker's RID-specific publish graph into the tray's single-file publish
    // and breaks it.
    // S17c: the Backup tab no longer builds per-destination connection
    // fields at all (GitHub/Drive blocks, transport rows, ...) - that whole
    // area moved into BackupDestinationEditDialog, opened from
    // BackupDestinationsDialog ("Manage destinations..." below). The tab
    // keeps only a compact read-only summary (see _destinationsSummary) and
    // the shared schedule controls.
    private ListView? _destinationsSummary;
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

    // S17c: same seam as _backupConfigPath, for backup-status.json - added
    // now because the Backup tab's read-only destination summary (and
    // BackupDestinationsDialog's Remove flow, which calls
    // BackupStatus.RemoveDestination) both need to read/write it, and
    // neither may ever touch the real %LOCALAPPDATA% path from a test - see
    // SettingsFormSmokeTests.SuiteNeverTouchesTheRealBackupConfigOrStatusFiles,
    // the guard test this seam keeps passing.
    private readonly string _backupStatusPath;

    /// <summary>
    /// Fires whenever BackupDestinationsDialog reports that it actually
    /// persisted a change (add/edit/remove) - see that dialog's own
    /// <c>Changed</c> property. TrayApplicationContext subscribes to this so
    /// it can re-evaluate backup health PROMPTLY instead of waiting up to
    /// PollIntervalMinutes for the next scheduled poll - without this, a
    /// removed (or newly-failing) destination's tray badge would linger
    /// until the next poll fired, which is exactly the "the badge lingers
    /// and the user thinks it failed" outcome the task brief calls out.
    /// </summary>
    public event Action? BackupDestinationsChanged;

    public SettingsForm(AppSettings current, string? backupConfigPath = null, string? backupStatusPath = null)
    {
        _backupConfigPath = backupConfigPath ?? BackupConfig.DefaultPath();
        _backupStatusPath = backupStatusPath ?? BackupStatus.DefaultPath();
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
    /// S17c: the destination summary, Frequency/Time, and action buttons -
    /// everything the Backup tab keeps once "Manage destinations..." took
    /// over every per-destination connection field (GitHub/Drive blocks,
    /// transport rows, the old "Back up to" selector and its hard "GitHub
    /// plus one cloud/NAS destination" limit - see BackupDestinationsDialog
    /// and BackupDestinationEditDialog). Fix round 1 originally measured
    /// this tab at 705px with both destinations' connection fields always
    /// visible; S16 brought that down to 684px by showing only one block at
    /// a time behind a selector; moving every per-destination field into its
    /// own dialog altogether removes that whole area from this tab's height
    /// budget instead of merely economizing on it - see
    /// SettingsFormHeightStaysWithinTheDisplayBudget for the re-measured
    /// number.
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

        var helpButton = NewFlatButton("Help", palette);
        helpButton.Location = new Point(PagePadX + fullWidth - helpButton.Width, y);
        helpButton.Click += (_, _) =>
        {
            using var dlg = new BackupHelpDialog(palette);
            dlg.ShowDialog(this);
        };
        page.Controls.Add(helpButton);

        // S7/S8: schedule-robustness and backup-health-staleness settings do
        // not fit as plain rows within the Backup tab's display budget (see
        // the design spec's layout constraint) - a single button placed
        // inline beside the existing Help button (same NewFlatButton type,
        // so it adds no extra row height) opens BackupAdvancedDialog instead
        // of adding any new row.
        var advancedButton = NewFlatButton("Advanced...", palette);
        advancedButton.Location = new Point(helpButton.Left - 8 - advancedButton.Width, y);
        advancedButton.Click += (_, _) => OnOpenAdvancedDialog(palette);
        page.Controls.Add(advancedButton);
        y += helpButton.Height + RowGap;

        var summaryLabel = NewSectionLabel("Backup destinations", palette, y);
        page.Controls.Add(summaryLabel);
        y += summaryLabel.PreferredHeight + 2;

        // S17c: compact, read-only - name, kind, enabled, last-run health -
        // fixed height regardless of how many destinations are configured
        // (scrolls internally, like RestoreDialog's own snapshot list),
        // never AutoScroll on the page itself. Populated by
        // RefreshDestinationsSummary, which BuildBackupPage calls once here
        // and OnManageDestinationsClicked calls again after the dialog
        // closes, so a destination added/edited/removed there is reflected
        // immediately without reopening Settings.
        _destinationsSummary = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            Location = new Point(PagePadX, y),
            Size = new Size(fullWidth, 90),
            Font = BaseFont,
            BackColor = palette.Back,
            ForeColor = palette.Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _destinationsSummary.Columns.Add("Name", 150);
        _destinationsSummary.Columns.Add("Kind", 140);
        _destinationsSummary.Columns.Add("Enabled", 60);
        _destinationsSummary.Columns.Add("Last run", fullWidth - 150 - 140 - 60);
        page.Controls.Add(_destinationsSummary);
        y += _destinationsSummary.Height + 2;

        var manageButton = NewFlatButton("Manage destinations...", palette);
        manageButton.Location = new Point(PagePadX, y);
        manageButton.Click += (_, _) => OnManageDestinationsClicked(palette);
        page.Controls.Add(manageButton);
        y += manageButton.Height + RowGap;

        RefreshDestinationsSummary();

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
    /// S17c: opens BackupDestinationsDialog against the SAVED backup.json/
    /// backup-status.json (same "operate on disk, not on this form's
    /// possibly-unsaved state" rule OnOpenRestoreDialog and
    /// RunBackupNowAsync already follow) - every Add/Edit/Remove inside that
    /// dialog persists immediately on its own, independent of this dialog's
    /// own OK/Cancel, mirroring "Save and register schedule"'s existing
    /// independence from the outer dialog. After it closes, the read-only
    /// summary is refreshed regardless of whether anything changed (cheap),
    /// and - only when the dialog reports <c>Changed</c> - <see
    /// cref="BackupDestinationsChanged"/> fires so a subscriber (the tray)
    /// can re-evaluate backup health promptly instead of waiting for its
    /// next scheduled poll.
    /// </summary>
    private void OnManageDestinationsClicked(Palette palette)
    {
        using var dialog = new BackupDestinationsDialog(palette, _backupConfigPath, _backupStatusPath);
        dialog.ShowDialog(this);
        RefreshDestinationsSummary();
        if (dialog.Changed)
            BackupDestinationsChanged?.Invoke();
    }

    /// <summary>
    /// Repopulates <see cref="_destinationsSummary"/> from whatever is
    /// currently saved on disk - called once by BuildBackupPage and again by
    /// OnManageDestinationsClicked after the destinations dialog closes, so
    /// an add/edit/remove there is reflected here immediately without
    /// reopening Settings.
    /// </summary>
    private void RefreshDestinationsSummary()
    {
        var config = BackupConfig.Load(_backupConfigPath);
        var status = BackupStatus.Load(_backupStatusPath);

        _destinationsSummary!.Items.Clear();
        foreach (var destination in config.Destinations)
        {
            _destinationsSummary.Items.Add(new ListViewItem(new[]
            {
                destination.Name,
                BackupDestinationNaming.KindDisplayName(destination.Kind, destination.SyncProvider),
                destination.Enabled ? "Yes" : "No",
                LastRunSummary(status.For(destination.Id)),
            }));
        }
    }

    private static string LastRunSummary(DestinationStatus status)
    {
        if (status.LastAttemptUtc is null)
            return "Never run";
        if (status.LastOutcome == BackupOutcome.Failed)
            return "Failed";
        return status.LastSuccessUtc is { } last
            ? $"OK - {ClaudeCounter.Core.TimeText.Ago(last, DateTimeOffset.UtcNow)}"
            : "Never run";
    }

    /// <summary>
    /// S7/S8: opens BackupAdvancedDialog seeded from the current in-memory
    /// values of the schedule-robustness fields (which themselves start out
    /// seeded from disk in BuildBackupPage), and on DialogResult.OK writes
    /// the dialog's result back onto those same fields. On Cancel (or
    /// closing via Esc/the X button), nothing changes. Nothing is persisted
    /// here; like every other Backup tab field, that only happens when "Save
    /// and register schedule" is clicked (see OnSaveBackupSchedule).
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

        using var dialog = new BackupAdvancedDialog(palette, schedule);
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
    }

    /// <summary>
    /// S9b: opens RestoreDialog against whatever is currently SAVED to
    /// backup.json - not the possibly-unsaved edits sitting in this form's
    /// own textboxes right now. Mirrors RunBackupNowAsync, which reloads the
    /// same way for the same reason: "Back up now" launches ClaudeBackup.exe,
    /// which itself only ever reads backup.json from disk, so restore
    /// operating on the same saved snapshot of config keeps both actions
    /// consistent with each other. Refuses to even open the dialog when no
    /// destination is enabled, rather than leaving RestoreDialog to show an
    /// empty "no destination" state - a quick, purely informational
    /// MessageBox reads better than an empty dialog for the common case
    /// (nothing configured yet) this guards against.
    /// </summary>
    private void OnOpenRestoreDialog(Palette palette)
    {
        var config = BackupConfig.Load(_backupConfigPath);
        if (!config.Destinations.Any(d => d.Enabled))
        {
            MessageBox.Show(this,
                "No backup destination is enabled. Add and enable one first, via " +
                "\"Manage destinations...\" on the Backup tab.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new RestoreDialog(palette, config, new ProcessRunner());
        dialog.ShowDialog(this);
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

    /// <summary>
    /// S17c: "Save and register schedule" now only owns the schedule itself
    /// (Frequency/Time/Advanced) - every per-destination field (enable,
    /// connection details, Include/Exclude, retention) is saved immediately
    /// by BackupDestinationsDialog/BackupDestinationEditDialog the moment
    /// each is added/edited, independent of this button. This button still
    /// reads the freshly-saved config back from disk (not a possibly-stale
    /// in-memory copy) purely to decide whether ANY destination is enabled,
    /// for the register-vs-unregister decision below.
    /// </summary>
    private void OnSaveBackupSchedule(object? sender, EventArgs e)
    {
        var time = _backupTime!.Text.Trim();
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            MessageBox.Show(this, "Time must be a 24-hour value in HH:mm format, e.g. 09:00.",
                "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var config = BackupConfig.Load(_backupConfigPath);

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

        config.Save(_backupConfigPath);

        // Removing/disabling every destination and saving must not silently
        // recreate a task that would run a backup nobody asked for anymore.
        var destinationEnabled = config.Destinations.Any(d => d.Enabled);
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

}

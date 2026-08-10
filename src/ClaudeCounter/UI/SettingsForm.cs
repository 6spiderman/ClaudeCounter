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
    // Visible - _backupSelectionTarget (GitHub / Google Drive) controls
    // which. Every field below is therefore a genuinely separate Control per
    // destination (not a single shared control whose content gets swapped),
    // so OnSaveBackupSchedule can read both destinations' values directly at
    // any time regardless of which block currently happens to be on screen.
    private CheckBox? _backupGithubEnabled;
    private TextBox? _backupGithubUrl;
    private TextBox? _backupGithubBranch;
    private TextBox? _backupGithubInclude;
    private TextBox? _backupGithubExclude;
    private CheckBox? _backupDriveEnabled;
    private TextBox? _backupDriveRemote;
    private TextBox? _backupDriveInclude;
    private TextBox? _backupDriveExclude;
    private ComboBox? _backupSelectionTarget;
    private Panel? _backupGithubBlock;
    private Panel? _backupDriveBlock;
    private ComboBox? _backupFrequency;
    private TextBox? _backupTime;

    // Backs every per-field (i) popup on the Backup tab. A single shared
    // instance (not one per button) because ToolTip.Show already positions
    // and dismisses independently per call; only created when the Backup tab
    // is (BackupTaskManager.WorkerAvailable()), and disposed in Dispose below
    // since it is a Component, not a Control, and would otherwise outlive the
    // form's own Controls.Clear()-driven cleanup.
    private ToolTip? _helpTip;

    public SettingsForm(AppSettings current)
    {
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

        return (page, y + 10);
    }

    /// <summary>
    /// Destination selector, Frequency/Time, action buttons - all shared,
    /// visible regardless of which destination is selected. The GitHub and
    /// Drive connection fields (enable checkbox, remote URL/branch or rclone
    /// remote) plus that destination's own Include/Exclude live in two
    /// separate blocks (see BuildGithubBlock / BuildDriveBlock) that are
    /// both built and added to the page up front, but only one is ever
    /// Visible at a time - see the selector's SelectedIndexChanged handler
    /// below. Fix round 1: this used to show BOTH destinations' connection
    /// fields simultaneously with only the Include/Exclude boxes switching,
    /// which made the Backup tab tall enough (705px) to run off the bottom
    /// of a 1366x768 display at 100% DPI. Showing only one destination's
    /// fields at a time both fixes the height and makes "which destination
    /// am I editing" unambiguous without any extra dynamic labeling - the
    /// visible block IS the answer.
    ///
    /// Only called when BackupTaskManager.WorkerAvailable() - current values
    /// are loaded from BackupConfig.DefaultPath().
    /// </summary>
    private (Panel Page, int Height) BuildBackupPage(Palette palette)
    {
        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        var page = new Panel { Dock = DockStyle.Fill, BackColor = palette.Back, Visible = false };
        var y = PageTopY;
        var fullWidth = DialogWidth - PagePadX * 2;
        var rightEdgeX = PagePadX + fullWidth - InfoButtonSize;

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
        y += helpButton.Height + RowGap;

        var selectorLabel = NewSectionLabel("Editing settings for", palette, y);
        page.Controls.Add(selectorLabel);
        y += selectorLabel.PreferredHeight + 2;

        _backupSelectionTarget = NewCombo(palette, 150);
        _backupSelectionTarget.Location = new Point(PagePadX, y);
        _backupSelectionTarget.Items.Add("GitHub");
        _backupSelectionTarget.Items.Add("Google Drive");
        _backupSelectionTarget.SelectedIndex = 0;
        page.Controls.Add(_backupSelectionTarget);
        y += _backupSelectionTarget.Height + RowGap;

        var (githubBlock, githubBlockHeight) = BuildGithubBlock(config.Github, palette, fullWidth, rightEdgeX);
        var (driveBlock, driveBlockHeight) = BuildDriveBlock(config.Drive, palette, fullWidth, rightEdgeX);
        githubBlock.Location = new Point(0, y);
        driveBlock.Location = new Point(0, y);
        githubBlock.Visible = true;
        driveBlock.Visible = false;
        // Both blocks are added regardless of the selector's starting value -
        // only Visible toggles thereafter - so every control inside both
        // (including the ones not currently shown) is fully constructed and
        // reachable by OnSaveBackupSchedule the whole time the dialog is
        // open, not just while its block happens to be on screen.
        page.Controls.Add(driveBlock);
        page.Controls.Add(githubBlock);
        _backupGithubBlock = githubBlock;
        _backupDriveBlock = driveBlock;
        _backupSelectionTarget.SelectedIndexChanged += (_, _) =>
        {
            var showGithub = _backupSelectionTarget.SelectedIndex == 0;
            _backupGithubBlock!.Visible = showGithub;
            _backupDriveBlock!.Visible = !showGithub;
        };
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
        var runNowX = PagePadX + fullWidth - runNowButton.Width;
        var saveX = runNowX - 8 - saveScheduleButton.Width;
        runNowButton.Location = new Point(runNowX, y);
        saveScheduleButton.Location = new Point(saveX, y);
        runNowButton.Click += async (_, _) => await RunBackupNowAsync();
        saveScheduleButton.Click += OnSaveBackupSchedule;
        page.Controls.Add(saveScheduleButton);
        page.Controls.Add(runNowButton);
        y += Math.Max(runNowButton.Height, saveScheduleButton.Height) + RowGap;

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
    private (Panel Block, int Height) BuildGithubBlock(GitTarget target, Palette palette, int fullWidth, int rightEdgeX)
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
        y += includeLabel.PreferredHeight + 2;
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
    /// Drive's connection fields (enable, rclone remote) plus Drive's own
    /// Include/Exclude. See BuildGithubBlock's doc comment for the layout
    /// reasoning - identical here, just for the Drive target.
    /// </summary>
    private (Panel Block, int Height) BuildDriveBlock(DriveTarget target, Palette palette, int fullWidth, int rightEdgeX)
    {
        var block = new Panel { BackColor = palette.Back };
        var y = 0;

        _backupDriveEnabled = NewCheckBox("Back up to Google Drive (rclone)", target.Enabled, palette);
        _backupDriveEnabled.Location = new Point(PagePadX, y);
        block.Controls.Add(_backupDriveEnabled);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.DriveEnabled);
        y += _backupDriveEnabled.Height + RowGap;

        block.Controls.Add(NewFieldLabel("Rclone remote", palette, y));
        _backupDriveRemote = NewTextBox(target.RcloneRemote, palette, 130);
        _backupDriveRemote.Location = new Point(FieldX, y);
        block.Controls.Add(_backupDriveRemote);
        AddInfoButton(block, palette, FieldX + 130 + 8, y + 3, BackupHelpText.RcloneRemote);
        y += _backupDriveRemote.Height + RowGap;

        var includeLabel = NewSectionLabel("Include (one pattern per line)", palette, y);
        block.Controls.Add(includeLabel);
        AddInfoButton(block, palette, rightEdgeX, y, BackupHelpText.Include);
        y += includeLabel.PreferredHeight + 2;
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
        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        config.Github.Enabled = _backupGithubEnabled!.Checked;
        config.Github.RemoteUrl = remoteUrl;
        config.Github.Branch = _backupGithubBranch!.Text.Trim();
        config.Github.Include = SplitLines(_backupGithubInclude!.Text);
        config.Github.Exclude = SplitLines(_backupGithubExclude!.Text);
        config.Drive.Enabled = _backupDriveEnabled!.Checked;
        config.Drive.RcloneRemote = rcloneRemote;
        config.Drive.Include = SplitLines(_backupDriveInclude!.Text);
        config.Drive.Exclude = SplitLines(_backupDriveExclude!.Text);
        config.Schedule.Frequency = BackupFrequencies[_backupFrequency!.SelectedIndex].Value;
        config.Schedule.Time = time;

        config.Save(BackupConfig.DefaultPath());

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
    /// A checkbox with a fully custom-painted indicator box, themed via
    /// Palette. Subclassing CheckBox itself (rather than a plain Control, as
    /// TabButton does) keeps all of the base class's input handling for
    /// free - click-to-toggle, Space-to-toggle, Checked/CheckedChanged and
    /// Tab-order participate exactly as on a stock CheckBox; only painting
    /// and preferred-size are overridden.
    /// </summary>
    private sealed class ThemedCheckBox : CheckBox
    {
        private const int BoxSize = 16;
        private const int BoxTextGap = 8;

        private readonly Palette _palette;

        public ThemedCheckBox(Palette palette)
        {
            _palette = palette;
            FlatStyle = FlatStyle.Flat; // GDI+-rendered, not FlatStyle.System - required for OnPaint to be honored
            BackColor = Color.Transparent;
            ForeColor = palette.Fore;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var textSize = TextRenderer.MeasureText(Text, Font);
            return new Size(
                BoxSize + BoxTextGap + textSize.Width + 2,
                Math.Max(BoxSize, textSize.Height) + 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? _palette.Back);

            var boxRect = new Rectangle(0, (Height - BoxSize) / 2, BoxSize, BoxSize);
            using (var fillBrush = new SolidBrush(Checked ? _palette.Fore : _palette.Back))
                e.Graphics.FillRectangle(fillBrush, boxRect);
            using (var borderPen = new Pen(_palette.Border))
                e.Graphics.DrawRectangle(borderPen, boxRect.X, boxRect.Y, boxRect.Width - 1, boxRect.Height - 1);

            if (Checked)
            {
                // Drawn in the palette's Back color against the Fore-filled
                // box: the same figure/ground pair as the rest of the theme,
                // just inverted, so it reads clearly in both light and dark.
                using var tickPen = new Pen(_palette.Back, 2f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                Point[] tick =
                [
                    new Point(boxRect.X + 3, boxRect.Y + 8),
                    new Point(boxRect.X + 6, boxRect.Y + 11),
                    new Point(boxRect.X + 13, boxRect.Y + 4),
                ];
                e.Graphics.DrawLines(tickPen, tick);
            }

            var textRect = new Rectangle(BoxSize + BoxTextGap, 0, Width - BoxSize - BoxTextGap, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, _palette.Fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
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

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
    private CheckBox _criticalAlerts = null!;
    private CheckBox _maxedAlerts = null!;
    private CheckBox _alertFiveHour = null!;
    private CheckBox _alertSevenDay = null!;
    private CheckBox _alertOpus = null!;
    private CheckBox _alertSonnet = null!;
    private ComboBox _placement = null!;

    // Only created when BackupTaskManager.WorkerAvailable() - the Backup tab is
    // entirely absent (fields stay null, and no tab button is created) when
    // ClaudeBackup.exe is not installed next to the tray exe. This project has
    // no ProjectReference to ClaudeBackup.csproj - the ClaudeBackup types used
    // below (BackupConfig, ScheduleConfig, ...) live in ClaudeCounter.Shared
    // instead. Do not add a reference to ClaudeBackup.csproj here; it drags the
    // worker's RID-specific publish graph into the tray's single-file publish
    // and breaks it.
    private CheckBox? _backupGithubEnabled;
    private TextBox? _backupGithubUrl;
    private TextBox? _backupGithubBranch;
    private CheckBox? _backupDriveEnabled;
    private TextBox? _backupDriveRemote;
    private TextBox? _backupInclude;
    private TextBox? _backupExclude;
    private ComboBox? _backupFrequency;
    private TextBox? _backupTime;

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

        // Fill must be added last: with exactly one control docked to each of
        // Top/Bottom and Fill added after both, the remaining client area goes
        // to contentHost unambiguously.
        Controls.Add(tabStrip);
        Controls.Add(bottomBar);
        Controls.Add(contentHost);

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

        return (page, y + 10);
    }

    /// <summary>
    /// GitHub group, Drive group, Include/Exclude, Frequency/Time, action
    /// buttons. Only called when BackupTaskManager.WorkerAvailable() - current
    /// values are loaded from BackupConfig.DefaultPath().
    /// </summary>
    private (Panel Page, int Height) BuildBackupPage(Palette palette)
    {
        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        var page = new Panel { Dock = DockStyle.Fill, BackColor = palette.Back, Visible = false };
        var y = PageTopY;
        var fullWidth = DialogWidth - PagePadX * 2;

        _backupGithubEnabled = NewCheckBox("Back up to a GitHub repo", config.Github.Enabled, palette);
        _backupGithubEnabled.Location = new Point(PagePadX, y);
        page.Controls.Add(_backupGithubEnabled);
        y += _backupGithubEnabled.Height + RowGap;

        var urlLabel = NewSectionLabel("Remote URL", palette, y);
        page.Controls.Add(urlLabel);
        y += urlLabel.PreferredHeight + 2;

        _backupGithubUrl = NewTextBox(config.Github.RemoteUrl, palette, fullWidth);
        _backupGithubUrl.Location = new Point(PagePadX, y);
        page.Controls.Add(_backupGithubUrl);
        y += _backupGithubUrl.Height + RowGap;

        // I2: the security model's non-negotiable guardrail - ClaudeCounter
        // has no way to call the GitHub API and check a repo's visibility, so
        // it cannot enforce privacy. The one thing it can do is make sure the
        // user is not left assuming it was checked for them.
        var privacyCaption = NewSubtleLabel(
            "This repo must be private. ClaudeCounter cannot verify that automatically.",
            palette, fullWidth);
        privacyCaption.Location = new Point(PagePadX, y);
        page.Controls.Add(privacyCaption);
        y += privacyCaption.PreferredHeight + RowGap;

        page.Controls.Add(NewFieldLabel("Branch", palette, y));
        _backupGithubBranch = NewTextBox(config.Github.Branch, palette, 130);
        _backupGithubBranch.Location = new Point(FieldX, y);
        page.Controls.Add(_backupGithubBranch);
        y += _backupGithubBranch.Height + RowGap;

        _backupDriveEnabled = NewCheckBox("Back up to Google Drive (rclone)", config.Drive.Enabled, palette);
        _backupDriveEnabled.Location = new Point(PagePadX, y);
        page.Controls.Add(_backupDriveEnabled);
        y += _backupDriveEnabled.Height + RowGap;

        page.Controls.Add(NewFieldLabel("Rclone remote", palette, y));
        _backupDriveRemote = NewTextBox(config.Drive.RcloneRemote, palette, 130);
        _backupDriveRemote.Location = new Point(FieldX, y);
        page.Controls.Add(_backupDriveRemote);
        y += _backupDriveRemote.Height + RowGap;

        var includeLabel = NewSectionLabel("Include (one pattern per line)", palette, y);
        page.Controls.Add(includeLabel);
        y += includeLabel.PreferredHeight + 2;
        _backupInclude = NewTextBox(string.Join(Environment.NewLine, config.Include), palette, fullWidth, multiline: true, height: 55);
        _backupInclude.Location = new Point(PagePadX, y);
        page.Controls.Add(_backupInclude);
        y += _backupInclude.Height + RowGap;

        var excludeLabel = NewSectionLabel("Exclude (one pattern per line)", palette, y);
        page.Controls.Add(excludeLabel);
        y += excludeLabel.PreferredHeight + 2;
        _backupExclude = NewTextBox(string.Join(Environment.NewLine, config.Exclude), palette, fullWidth, multiline: true, height: 55);
        _backupExclude.Location = new Point(PagePadX, y);
        page.Controls.Add(_backupExclude);
        y += _backupExclude.Height + RowGap;

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

    private static CheckBox NewCheckBox(string text, bool @checked, Palette palette) => new()
    {
        Text = text,
        AutoSize = true,
        Checked = @checked,
        Font = BaseFont,
        ForeColor = palette.Fore,
        BackColor = Color.Transparent,
        FlatStyle = FlatStyle.Flat,
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

        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        config.Github.Enabled = _backupGithubEnabled!.Checked;
        config.Github.RemoteUrl = remoteUrl;
        config.Github.Branch = _backupGithubBranch!.Text.Trim();
        config.Drive.Enabled = _backupDriveEnabled!.Checked;
        config.Drive.RcloneRemote = rcloneRemote;
        config.Include = SplitLines(_backupInclude!.Text);
        config.Exclude = SplitLines(_backupExclude!.Text);
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
        settings.CriticalAlertsEnabled = _criticalAlerts.Checked;
        settings.MaxedAlertsEnabled = _maxedAlerts.Checked;
        settings.AlertFiveHour = _alertFiveHour.Checked;
        settings.AlertSevenDay = _alertSevenDay.Checked;
        settings.AlertSevenDayOpus = _alertOpus.Checked;
        settings.AlertSevenDaySonnet = _alertSonnet.Checked;
        settings.PopupPlacement = _placement.SelectedIndex == 1
            ? PopupPlacement.Centered : PopupPlacement.NearTray;
    }

    /// <summary>
    /// A single flat, themed tab button. A plain Control (not a Button) so it
    /// can be fully custom-painted - the active tab is marked with a solid
    /// underline plus a brighter foreground rather than any 3D chrome.
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
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
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
        }
    }
}

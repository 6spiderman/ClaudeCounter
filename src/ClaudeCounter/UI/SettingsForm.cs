using System.Globalization;
using ClaudeBackup;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

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

    private readonly ComboBox _intervalCombo;
    private readonly NumericUpDown _warnInput;
    private readonly NumericUpDown _criticalInput;
    private readonly CheckBox _autostartCheck;
    private readonly CheckBox _updateCheck;
    private readonly CheckBox _criticalAlerts;
    private readonly CheckBox _maxedAlerts;
    private readonly CheckBox _alertFiveHour;
    private readonly CheckBox _alertSevenDay;
    private readonly CheckBox _alertOpus;
    private readonly CheckBox _alertSonnet;
    private readonly ComboBox _placement;

    // Only created when BackupTaskManager.WorkerAvailable() - the Backup group
    // is entirely absent (fields stay null) when ClaudeBackup.exe is not
    // installed next to the tray exe. This project has no ProjectReference to
    // ClaudeBackup.csproj - the ClaudeBackup types used below (BackupConfig,
    // ScheduleConfig, ...) live in ClaudeCounter.Shared instead. Do not add a
    // reference to ClaudeBackup.csproj here; it drags the worker's RID-specific
    // publish graph into the tray's single-file publish and breaks it.
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
        Text = "ClaudeCounter Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var backupAvailable = BackupTaskManager.WorkerAvailable();
        if (backupAvailable)
        {
            // The Backup group's natural height (860) can exceed a small
            // laptop's working area, which would push the OK/Cancel row (and
            // "Save and register schedule") off-screen on a FixedDialog that
            // cannot be resized. Clamp to the screen and let the layout panel
            // scroll for whatever does not fit.
            var screenHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 860;
            ClientSize = new Size(420, Math.Min(860, screenHeight - 80));
        }
        else
        {
            ClientSize = new Size(360, 470);
        }
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        Icon = Shell.AppIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 2,
            RowCount = 13,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        layout.Controls.Add(new Label { Text = "Update frequency", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _intervalCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        foreach (var minutes in AppSettings.IntervalPresets)
            _intervalCombo.Items.Add($"{minutes} min");
        _intervalCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.IntervalPresets, current.PollIntervalMinutes));
        layout.Controls.Add(_intervalCombo, 1, 0);

        var note = new Label
        {
            Text = "Intervals under 3 min may be rate limited; the app backs off automatically.",
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            ForeColor = SystemColors.GrayText,
        };
        layout.Controls.Add(note, 0, 1);
        layout.SetColumnSpan(note, 2);

        layout.Controls.Add(new Label { Text = "Warn threshold (%)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _warnInput = new NumericUpDown { Minimum = 1, Maximum = 99, Value = current.WarnThreshold, Width = 70 };
        layout.Controls.Add(_warnInput, 1, 2);

        layout.Controls.Add(new Label { Text = "Critical threshold (%)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _criticalInput = new NumericUpDown { Minimum = 2, Maximum = 100, Value = current.CriticalThreshold, Width = 70 };
        layout.Controls.Add(_criticalInput, 1, 3);

        _autostartCheck = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = current.AutostartEnabled };
        layout.Controls.Add(_autostartCheck, 0, 4);
        layout.SetColumnSpan(_autostartCheck, 2);

        _updateCheck = new CheckBox
        {
            Text = "Check for updates automatically",
            AutoSize = true,
            Checked = current.CheckForUpdates,
        };
        layout.Controls.Add(_updateCheck, 0, 5);
        layout.SetColumnSpan(_updateCheck, 2);

        var alertsHeader = new Label
        {
            Text = "Alerts",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
        };
        layout.Controls.Add(alertsHeader, 0, 6);
        layout.SetColumnSpan(alertsHeader, 2);

        _criticalAlerts = new CheckBox
        {
            Text = "Popup when a window hits the critical threshold",
            AutoSize = true,
            Checked = current.CriticalAlertsEnabled,
        };
        layout.Controls.Add(_criticalAlerts, 0, 7);
        layout.SetColumnSpan(_criticalAlerts, 2);

        _maxedAlerts = new CheckBox
        {
            Text = "Popup when a window hits 100%",
            AutoSize = true,
            Checked = current.MaxedAlertsEnabled,
        };
        layout.Controls.Add(_maxedAlerts, 0, 8);
        layout.SetColumnSpan(_maxedAlerts, 2);

        _alertFiveHour = new CheckBox { Text = "5-hour session", AutoSize = true, Checked = current.AlertFiveHour };
        layout.Controls.Add(_alertFiveHour, 0, 9);

        _alertSevenDay = new CheckBox { Text = "Weekly (all models)", AutoSize = true, Checked = current.AlertSevenDay };
        layout.Controls.Add(_alertSevenDay, 1, 9);

        _alertOpus = new CheckBox { Text = "Weekly (Opus)", AutoSize = true, Checked = current.AlertSevenDayOpus };
        layout.Controls.Add(_alertOpus, 0, 10);

        _alertSonnet = new CheckBox { Text = "Weekly (Sonnet)", AutoSize = true, Checked = current.AlertSevenDaySonnet };
        layout.Controls.Add(_alertSonnet, 1, 10);

        layout.Controls.Add(new Label { Text = "Popup placement", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 11);
        _placement = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        _placement.Items.Add("Near tray");
        _placement.Items.Add("Centered");
        _placement.SelectedIndex = current.PopupPlacement == PopupPlacement.Centered ? 1 : 0;
        layout.Controls.Add(_placement, 1, 11);

        var row = 12;
        if (backupAvailable)
            row = BuildBackupGroup(layout, row);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
        };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var okButton = new Button { Text = "OK" };
        okButton.Click += OnOk;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 2);
        layout.RowCount = row + 1;

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    /// <summary>
    /// Adds the Backup group starting at <paramref name="row"/> and returns the
    /// next free row. Only called when BackupTaskManager.WorkerAvailable() -
    /// current values are loaded from BackupConfig.DefaultPath().
    /// </summary>
    private int BuildBackupGroup(TableLayoutPanel layout, int row)
    {
        var config = BackupConfig.Load(BackupConfig.DefaultPath());

        var header = new Label { Text = "Backup", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        layout.Controls.Add(header, 0, row);
        layout.SetColumnSpan(header, 2);
        row++;

        _backupGithubEnabled = new CheckBox
        {
            Text = "Back up to a GitHub repo",
            AutoSize = true,
            Checked = config.Github.Enabled,
        };
        layout.Controls.Add(_backupGithubEnabled, 0, row);
        layout.SetColumnSpan(_backupGithubEnabled, 2);
        row++;

        layout.Controls.Add(new Label { Text = "Remote URL", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        row++;
        _backupGithubUrl = new TextBox
        {
            Text = config.Github.RemoteUrl,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        layout.Controls.Add(_backupGithubUrl, 0, row);
        layout.SetColumnSpan(_backupGithubUrl, 2);
        row++;

        layout.Controls.Add(new Label { Text = "Branch", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _backupGithubBranch = new TextBox { Text = config.Github.Branch, Width = 130, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_backupGithubBranch, 1, row);
        row++;

        _backupDriveEnabled = new CheckBox
        {
            Text = "Back up to Google Drive (rclone)",
            AutoSize = true,
            Checked = config.Drive.Enabled,
        };
        layout.Controls.Add(_backupDriveEnabled, 0, row);
        layout.SetColumnSpan(_backupDriveEnabled, 2);
        row++;

        layout.Controls.Add(new Label { Text = "Rclone remote", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _backupDriveRemote = new TextBox
        {
            Text = config.Drive.RcloneRemote,
            Width = 130,
            Anchor = AnchorStyles.Left,
        };
        layout.Controls.Add(_backupDriveRemote, 1, row);
        row++;

        var includeLabel = new Label { Text = "Include (one pattern per line)", AutoSize = true, Anchor = AnchorStyles.Left };
        layout.Controls.Add(includeLabel, 0, row);
        layout.SetColumnSpan(includeLabel, 2);
        row++;
        _backupInclude = new TextBox
        {
            Text = string.Join(Environment.NewLine, config.Include),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 55,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        layout.Controls.Add(_backupInclude, 0, row);
        layout.SetColumnSpan(_backupInclude, 2);
        row++;

        var excludeLabel = new Label { Text = "Exclude (one pattern per line)", AutoSize = true, Anchor = AnchorStyles.Left };
        layout.Controls.Add(excludeLabel, 0, row);
        layout.SetColumnSpan(excludeLabel, 2);
        row++;
        _backupExclude = new TextBox
        {
            Text = string.Join(Environment.NewLine, config.Exclude),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 55,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        layout.Controls.Add(_backupExclude, 0, row);
        layout.SetColumnSpan(_backupExclude, 2);
        row++;

        layout.Controls.Add(new Label { Text = "Frequency", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _backupFrequency = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        foreach (var freq in BackupFrequencies)
            _backupFrequency.Items.Add(freq.Display);
        var freqIndex = Array.FindIndex(BackupFrequencies,
            f => f.Value == config.Schedule.Frequency.ToLowerInvariant());
        _backupFrequency.SelectedIndex = Math.Max(0, freqIndex);
        layout.Controls.Add(_backupFrequency, 1, row);
        row++;

        layout.Controls.Add(new Label { Text = "Time (24h HH:mm)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _backupTime = new TextBox { Text = config.Schedule.Time, Width = 80, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_backupTime, 1, row);
        row++;

        var backupButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
        };
        var runNowButton = new Button { Text = "Back up now", AutoSize = true };
        runNowButton.Click += (_, _) => BackupTaskManager.RunNow();
        var saveScheduleButton = new Button { Text = "Save and register schedule", AutoSize = true };
        saveScheduleButton.Click += OnSaveBackupSchedule;
        backupButtons.Controls.Add(runNowButton);
        backupButtons.Controls.Add(saveScheduleButton);
        layout.Controls.Add(backupButtons, 0, row);
        layout.SetColumnSpan(backupButtons, 2);
        row++;

        return row;
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

        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        config.Github.Enabled = _backupGithubEnabled!.Checked;
        config.Github.RemoteUrl = _backupGithubUrl!.Text.Trim();
        config.Github.Branch = _backupGithubBranch!.Text.Trim();
        config.Drive.Enabled = _backupDriveEnabled!.Checked;
        config.Drive.RcloneRemote = _backupDriveRemote!.Text.Trim();
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
}

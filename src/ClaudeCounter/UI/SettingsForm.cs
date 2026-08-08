using ClaudeBackup;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public sealed class SettingsForm : Form
{
    private static readonly string[] BackupFrequencyValues = ["daily", "weekly", "hourly"];

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
    // installed next to the tray exe.
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
        ClientSize = backupAvailable ? new Size(420, 860) : new Size(360, 470);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        Icon = Shell.AppIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
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
        _backupFrequency.Items.Add("Daily");
        _backupFrequency.Items.Add("Weekly");
        _backupFrequency.Items.Add("Hourly");
        var freqIndex = Array.IndexOf(BackupFrequencyValues, config.Schedule.Frequency.ToLowerInvariant());
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
        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        config.Github.Enabled = _backupGithubEnabled!.Checked;
        config.Github.RemoteUrl = _backupGithubUrl!.Text.Trim();
        config.Github.Branch = _backupGithubBranch!.Text.Trim();
        config.Drive.Enabled = _backupDriveEnabled!.Checked;
        config.Drive.RcloneRemote = _backupDriveRemote!.Text.Trim();
        config.Include = SplitLines(_backupInclude!.Text);
        config.Exclude = SplitLines(_backupExclude!.Text);
        config.Schedule.Frequency = BackupFrequencyValues[_backupFrequency!.SelectedIndex];
        config.Schedule.Time = _backupTime!.Text.Trim();

        config.Save(BackupConfig.DefaultPath());
        BackupTaskManager.Register(config.Schedule);

        MessageBox.Show(this, "Backup settings saved and the schedule registered.",
            "ClaudeCounter", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

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

using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public sealed class SettingsForm : Form
{
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

    public SettingsForm(AppSettings current)
    {
        Text = "ClaudeCounter Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 470);
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
        layout.Controls.Add(buttons, 0, 12);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

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

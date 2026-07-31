using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public sealed class SettingsForm : Form
{
    private readonly ComboBox _intervalCombo;
    private readonly NumericUpDown _warnInput;
    private readonly NumericUpDown _criticalInput;
    private readonly CheckBox _autostartCheck;
    private readonly CheckBox _updateCheck;

    public SettingsForm(AppSettings current)
    {
        Text = "ClaudeCounter Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 250);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;
        Icon = Shell.AppIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
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
        layout.Controls.Add(buttons, 0, 6);
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
    }
}

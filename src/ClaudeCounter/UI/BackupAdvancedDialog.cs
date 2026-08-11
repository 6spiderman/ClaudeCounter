using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// The Backup tab's "Advanced..." dialog: schedule-robustness settings
/// (StartWhenAvailable, network/battery behaviour, retry on failure - see
/// BackupTaskManager.BuildTaskXml, which is what actually encodes these into
/// the registered task) and Google Drive retention (KeepLastCount /
/// DeleteOlderThanDays on DriveTarget - see DriveRetention and
/// RcloneBackend). Both sets of settings do not fit as plain rows on the
/// Backup tab itself - see docs/superpowers/specs/2026-08-10-schedule-
/// robustness-and-retention.md's layout constraint and
/// SettingsFormSmokeTests.SettingsFormHeightStaysWithinTheDisplayBudget -
/// so they live behind this separate dialog instead, opened from a single
/// "Advanced..." button placed inline on the Backup tab's destination-
/// selector row (see SettingsForm.BuildBackupPage).
///
/// A thin, largely non-branching shell around plain CheckBox/NumericUpDown
/// state, like BackupHelpDialog and BackupPickerDialog - the caller
/// (SettingsForm) reads the result properties after ShowDialog() returns
/// DialogResult.OK and writes them onto ScheduleConfig / DriveTarget itself;
/// this dialog owns no config type and does not save anything on its own.
/// Never construct this (or any Form) from a test except via
/// SettingsFormSmokeTests' dedicated STA helper.
/// </summary>
public sealed class BackupAdvancedDialog : Form
{
    private const int DialogWidth = 480;
    private const int Pad = 16;
    private const int BottomBarHeight = 52;
    private const int RowGap = 8;
    private const int IndentX = Pad + 24;

    private readonly ThemedCheckBox _startWhenAvailable;
    private readonly ThemedCheckBox _runOnlyIfNetworkAvailable;
    private readonly ThemedCheckBox _disallowStartIfOnBatteries;
    private readonly ThemedCheckBox _stopIfGoingOnBatteries;
    private readonly ThemedCheckBox _restartOnFailure;
    private readonly NumericUpDown _restartIntervalMinutes;
    private readonly NumericUpDown _restartCount;
    private readonly NumericUpDown _backupStaleAfterDays;
    private readonly ThemedCheckBox _keepLastEnabled;
    private readonly NumericUpDown _keepLastCount;
    private readonly ThemedCheckBox _deleteOlderEnabled;
    private readonly NumericUpDown _deleteOlderDays;

    public bool StartWhenAvailable => _startWhenAvailable.Checked;
    public bool RunOnlyIfNetworkAvailable => _runOnlyIfNetworkAvailable.Checked;
    public bool DisallowStartIfOnBatteries => _disallowStartIfOnBatteries.Checked;
    public bool StopIfGoingOnBatteries => _stopIfGoingOnBatteries.Checked;
    public bool RestartOnFailure => _restartOnFailure.Checked;
    public int RestartIntervalMinutes => (int)_restartIntervalMinutes.Value;
    public int RestartCount => (int)_restartCount.Value;

    /// <summary>S11b: 0 means never warn about staleness - see ScheduleConfig.BackupStaleAfterDays's own doc comment.</summary>
    public int BackupStaleAfterDays => (int)_backupStaleAfterDays.Value;

    /// <summary>Null when the "Keep only the most recent" checkbox is unticked - the rule is off.</summary>
    public int? KeepLastCount => _keepLastEnabled.Checked ? (int)_keepLastCount.Value : null;

    /// <summary>Null when the "Delete backups older than" checkbox is unticked - the rule is off.</summary>
    public int? DeleteOlderThanDays => _deleteOlderEnabled.Checked ? (int)_deleteOlderDays.Value : null;

    public BackupAdvancedDialog(Palette palette, ScheduleConfig schedule, DriveTarget drive)
    {
        Text = "Advanced Backup Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        BackColor = palette.Back;
        ForeColor = palette.Fore;
        KeyPreview = true;

        var fullWidth = DialogWidth - Pad * 2;
        var y = Pad;

        var scheduleHeader = NewSectionLabel("Schedule robustness", palette, y, bold: true);
        Controls.Add(scheduleHeader);
        y += scheduleHeader.PreferredHeight + 4;

        _startWhenAvailable = NewCheckBox(
            "Run a missed backup as soon as possible", schedule.StartWhenAvailable, palette);
        _startWhenAvailable.Location = new Point(Pad, y);
        Controls.Add(_startWhenAvailable);
        y += _startWhenAvailable.Height + RowGap;

        _runOnlyIfNetworkAvailable = NewCheckBox(
            "Only run when a network is available", schedule.RunOnlyIfNetworkAvailable, palette);
        _runOnlyIfNetworkAvailable.Location = new Point(Pad, y);
        Controls.Add(_runOnlyIfNetworkAvailable);
        y += _runOnlyIfNetworkAvailable.Height + RowGap;

        // Deliberately default false (see ScheduleConfig's remarks) - the
        // opposite of what Windows itself defaults a new task to. Not
        // "corrected" here; the label states plainly what ticking it does.
        _disallowStartIfOnBatteries = NewCheckBox(
            "Don't start on battery", schedule.DisallowStartIfOnBatteries, palette);
        _disallowStartIfOnBatteries.Location = new Point(Pad, y);
        Controls.Add(_disallowStartIfOnBatteries);
        y += _disallowStartIfOnBatteries.Height + RowGap;

        _stopIfGoingOnBatteries = NewCheckBox(
            "Stop if the machine switches to battery", schedule.StopIfGoingOnBatteries, palette);
        _stopIfGoingOnBatteries.Location = new Point(Pad, y);
        Controls.Add(_stopIfGoingOnBatteries);
        y += _stopIfGoingOnBatteries.Height + RowGap;

        _restartOnFailure = NewCheckBox("Retry on failure", schedule.RestartOnFailure, palette);
        _restartOnFailure.Location = new Point(Pad, y);
        Controls.Add(_restartOnFailure);
        y += _restartOnFailure.Height + 2;

        var retryLabel1 = NewSectionLabel("every", palette, y);
        retryLabel1.Location = new Point(IndentX, y + 4);
        Controls.Add(retryLabel1);

        _restartIntervalMinutes = NewNumeric(palette, 1, 1440, Math.Max(1, schedule.RestartIntervalMinutes));
        _restartIntervalMinutes.Location = new Point(IndentX + retryLabel1.PreferredWidth + 6, y);
        Controls.Add(_restartIntervalMinutes);

        var retryLabel2 = NewSectionLabel(
            "min, up to", palette, y);
        retryLabel2.Location = new Point(_restartIntervalMinutes.Right + 6, y + 4);
        Controls.Add(retryLabel2);

        _restartCount = NewNumeric(palette, 1, 99, Math.Max(1, schedule.RestartCount));
        _restartCount.Location = new Point(retryLabel2.Right + 6, y);
        Controls.Add(_restartCount);

        var retryLabel3 = NewSectionLabel("times", palette, y);
        retryLabel3.Location = new Point(_restartCount.Right + 6, y + 4);
        Controls.Add(retryLabel3);

        y += _restartIntervalMinutes.Height + RowGap;

        _restartOnFailure.CheckedChanged += (_, _) =>
        {
            var enabled = _restartOnFailure.Checked;
            _restartIntervalMinutes.Enabled = enabled;
            _restartCount.Enabled = enabled;
        };
        _restartIntervalMinutes.Enabled = _restartOnFailure.Checked;
        _restartCount.Enabled = _restartOnFailure.Checked;

        // S11b: the staleness threshold behind BackupHealthState.Stale (see
        // BackupHealth.Evaluate) - lives here rather than on the Backup tab
        // itself for the same reason as everything else in this dialog: see
        // this class's own doc comment and
        // SettingsFormHeightStaysWithinTheDisplayBudget.
        y += 6;
        var healthHeader = NewSectionLabel("Backup health", palette, y, bold: true);
        Controls.Add(healthHeader);
        y += healthHeader.PreferredHeight + 4;

        var staleLabel = NewSectionLabel("Warn about stale backups after (days, 0 = never)", palette, y);
        Controls.Add(staleLabel);
        y += staleLabel.PreferredHeight + 2;

        _backupStaleAfterDays = NewNumeric(palette, 0, 365, Math.Clamp(schedule.BackupStaleAfterDays, 0, 365));
        _backupStaleAfterDays.Location = new Point(Pad, y);
        Controls.Add(_backupStaleAfterDays);
        y += _backupStaleAfterDays.Height + RowGap;

        y += 6;
        var driveHeader = NewSectionLabel("Google Drive retention", palette, y, bold: true);
        Controls.Add(driveHeader);
        y += driveHeader.PreferredHeight + 2;

        var driveNote = NewSubtleLabel(
            "When both are ticked, a backup is pruned if EITHER rule would remove it. " +
            "The single most recent backup is never deleted, whatever these settings say.",
            palette, fullWidth);
        driveNote.Location = new Point(Pad, y);
        Controls.Add(driveNote);
        y += driveNote.PreferredHeight + RowGap;

        _keepLastEnabled = NewCheckBox("Keep only the most recent", drive.KeepLastCount.HasValue, palette);
        _keepLastEnabled.Location = new Point(Pad, y);
        Controls.Add(_keepLastEnabled);

        _keepLastCount = NewNumeric(palette, 1, 3650, drive.KeepLastCount ?? 30);
        _keepLastCount.Location = new Point(_keepLastEnabled.Right + 6, y - 2);
        Controls.Add(_keepLastCount);

        var keepLastLabel = NewSectionLabel("backup(s)", palette, y);
        keepLastLabel.Location = new Point(_keepLastCount.Right + 6, y + 4);
        Controls.Add(keepLastLabel);

        y += _keepLastEnabled.Height + RowGap;

        _keepLastEnabled.CheckedChanged += (_, _) => _keepLastCount.Enabled = _keepLastEnabled.Checked;
        _keepLastCount.Enabled = _keepLastEnabled.Checked;

        _deleteOlderEnabled = NewCheckBox("Delete backups older than", drive.DeleteOlderThanDays.HasValue, palette);
        _deleteOlderEnabled.Location = new Point(Pad, y);
        Controls.Add(_deleteOlderEnabled);

        _deleteOlderDays = NewNumeric(palette, 1, 3650, drive.DeleteOlderThanDays ?? 90);
        _deleteOlderDays.Location = new Point(_deleteOlderEnabled.Right + 6, y - 2);
        Controls.Add(_deleteOlderDays);

        var deleteOlderLabel = NewSectionLabel("day(s)", palette, y);
        deleteOlderLabel.Location = new Point(_deleteOlderDays.Right + 6, y + 4);
        Controls.Add(deleteOlderLabel);

        y += _deleteOlderEnabled.Height + RowGap;

        _deleteOlderEnabled.CheckedChanged += (_, _) => _deleteOlderDays.Enabled = _deleteOlderEnabled.Checked;
        _deleteOlderDays.Enabled = _deleteOlderEnabled.Checked;

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = palette.BarBack };
        bottomBar.Controls.Add(new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(DialogWidth, 1),
            BackColor = palette.Border,
        });
        var cancelButton = NewDialogButton("Cancel", palette);
        cancelButton.DialogResult = DialogResult.Cancel;
        var okButton = NewDialogButton("OK", palette);
        okButton.DialogResult = DialogResult.OK;
        var cancelX = DialogWidth - Pad - cancelButton.Width;
        var okX = cancelX - 8 - okButton.Width;
        var btnY = (BottomBarHeight - okButton.Height) / 2;
        cancelButton.Location = new Point(cancelX, btnY);
        okButton.Location = new Point(okX, btnY);
        bottomBar.Controls.Add(cancelButton);
        bottomBar.Controls.Add(okButton);
        Controls.Add(bottomBar);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        ClientSize = new Size(DialogWidth, y + Pad + BottomBarHeight);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        };
    }

    private static Label NewSectionLabel(string text, Palette palette, int y, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        Font = bold ? new Font("Segoe UI Semibold", 9f) : new Font("Segoe UI", 9f),
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

    private static ThemedCheckBox NewCheckBox(string text, bool @checked, Palette palette) => new(palette)
    {
        Text = text,
        AutoSize = true,
        Checked = @checked,
        Font = new Font("Segoe UI", 9f),
    };

    private static NumericUpDown NewNumeric(Palette palette, int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Width = 60,
        Font = new Font("Segoe UI", 9f),
        BackColor = palette.Back,
        ForeColor = palette.Fore,
        BorderStyle = BorderStyle.FixedSingle,
    };

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
}

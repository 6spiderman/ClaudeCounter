using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// Settings -> Backup -> Advanced..., Linux edition of the Windows dialog:
/// the schedule options the systemd timer can honour (see
/// BackupTaskManager), plus backup health. "Only run when a network is
/// available" and "Stop if the machine switches to battery" have no
/// user-level systemd equivalent, so they are not offered - but their values
/// pass through unchanged, so a backup.json shared with Windows keeps them.
/// </summary>
public sealed class BackupAdvancedDialog : Window
{
    private readonly ScheduleConfig _original;
    private readonly CheckBox _startWhenAvailable;
    private readonly CheckBox _disallowStartIfOnBatteries;
    private readonly CheckBox _restartOnFailure;
    private readonly NumericUpDown _restartIntervalMinutes;
    private readonly NumericUpDown _restartCount;
    private readonly NumericUpDown _backupStaleAfterDays;

    public bool StartWhenAvailable => _startWhenAvailable.IsChecked == true;
    public bool RunOnlyIfNetworkAvailable => _original.RunOnlyIfNetworkAvailable;
    public bool DisallowStartIfOnBatteries => _disallowStartIfOnBatteries.IsChecked == true;
    public bool StopIfGoingOnBatteries => _original.StopIfGoingOnBatteries;
    public bool RestartOnFailure => _restartOnFailure.IsChecked == true;
    public int RestartIntervalMinutes => (int)(_restartIntervalMinutes.Value ?? 15);
    public int RestartCount => (int)(_restartCount.Value ?? 3);
    public int BackupStaleAfterDays => (int)(_backupStaleAfterDays.Value ?? 3);

    /// <summary>Show with <c>await dialog.ShowDialog&lt;bool&gt;(owner)</c>; true on OK.</summary>
    public BackupAdvancedDialog(ScheduleConfig schedule)
    {
        _original = schedule;

        Title = "Advanced Backup Settings";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _startWhenAvailable = new CheckBox { Content = "Run a missed backup as soon as possible", IsChecked = schedule.StartWhenAvailable };
        _disallowStartIfOnBatteries = new CheckBox { Content = "Don't start on battery", IsChecked = schedule.DisallowStartIfOnBatteries };
        _restartOnFailure = new CheckBox { Content = "Retry on failure", IsChecked = schedule.RestartOnFailure };
        _restartIntervalMinutes = Numeric(1, 1440, Math.Max(1, schedule.RestartIntervalMinutes));
        _restartCount = Numeric(1, 99, Math.Max(1, schedule.RestartCount));
        _backupStaleAfterDays = Numeric(0, 365, Math.Clamp(schedule.BackupStaleAfterDays, 0, 365));

        // The retry numbers only mean something while retrying is on.
        void SyncRetry() => _restartIntervalMinutes.IsEnabled = _restartCount.IsEnabled = _restartOnFailure.IsChecked == true;
        _restartOnFailure.IsCheckedChanged += (_, _) => SyncRetry();
        SyncRetry();

        var retryRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(28, 0, 0, 0),
            Children =
            {
                Label("every"),
                _restartIntervalMinutes,
                Label("min, up to"),
                _restartCount,
                Label("times"),
            },
        };

        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                Header("Schedule robustness"),
                _startWhenAvailable,
                _disallowStartIfOnBatteries,
                _restartOnFailure,
                retryRow,
                new TextBlock
                {
                    Text = "Waiting for a network connection and stopping when the laptop goes on battery are Windows-only options.",
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                },
                Header("Backup health"),
                new DockPanel
                {
                    Children =
                    {
                        Dock(_backupStaleAfterDays, Avalonia.Controls.Dock.Right),
                        new TextBlock
                        {
                            Text = "Warn about stale backups after (days, 0 = never)",
                            VerticalAlignment = VerticalAlignment.Center,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 12, 0),
                        },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 6, 0, 0),
                    Children = { cancel, ok },
                },
            },
        };
    }

    private static NumericUpDown Numeric(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        FormatString = "0",
        Width = 120,
    };

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    private static Control Dock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}

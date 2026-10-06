using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;
using ClaudeBackup;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

/// <summary>
/// Settings, in the same General / Alerts / Backup split as the Windows
/// build's SettingsForm, with the same wording and limits. The Backup tab
/// only appears when the ClaudeBackup worker is installed next to the app,
/// as on Windows.
/// </summary>
public sealed class SettingsWindow : Window
{
    private const double FieldWidth = 130;

    private readonly ComboBox _intervalCombo;
    private readonly NumericUpDown _warnInput;
    private readonly NumericUpDown _criticalInput;
    private readonly CheckBox _autostartCheck;
    private readonly CheckBox _updateCheck;

    private readonly CheckBox _warnAlerts;
    private readonly CheckBox _criticalAlerts;
    private readonly CheckBox _maxedAlerts;
    private readonly CheckBox _alertFiveHour;
    private readonly CheckBox _alertSevenDay;
    private readonly CheckBox _alertOpus;
    private readonly CheckBox _alertSonnet;
    private readonly ComboBox _placement;
    private readonly NumericUpDown _autoDismissInput;
    private readonly NumericUpDown _alertRepeatInput;

    private static readonly (string Value, string Display)[] BackupFrequencies =
    [
        ("daily", "Daily"),
        ("weekly", "Weekly"),
        ("hourly", "Hourly"),
    ];

    private readonly string _backupConfigPath;
    private readonly string _backupStatusPath;
    private ListBox? _destinationsSummary;
    private ComboBox? _backupFrequency;
    private TextBox? _backupTime;
    private ScheduleConfig _schedule = new();

    private readonly TabControl _tabs;
    private readonly TextBlock _error;
    private readonly int _warnDefault;
    private readonly int _criticalDefault;

    /// <summary>Raised once the user confirms; call <see cref="ApplyTo"/> to read the values.</summary>
    public event Action? Saved;

    /// <summary>Raised when destinations were added, edited or removed, so the tray can re-check backup health.</summary>
    public event Action? BackupDestinationsChanged;

    public SettingsWindow(AppSettings current, string? backupConfigPath = null, string? backupStatusPath = null)
    {
        _backupConfigPath = backupConfigPath ?? BackupConfig.DefaultPath();
        _backupStatusPath = backupStatusPath ?? BackupStatus.DefaultPath();

        _warnDefault = current.WarnThreshold;
        _criticalDefault = current.CriticalThreshold;

        Title = "ClaudeCounter Settings";
        // Wide enough for the Backup tab's destinations table and its row of
        // three buttons.
        Width = 560;
        // Height follows the content rather than a fixed number: text
        // metrics depend on the desktop's fonts and scaling, and a fixed
        // height clipped the OK/Cancel row on Kubuntu/Plasma.
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        // --- General ---
        _intervalCombo = new ComboBox
        {
            ItemsSource = AppSettings.IntervalPresets.Select(m => $"{m} min").ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.IntervalPresets, current.PollIntervalMinutes)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = FieldWidth,
        };
        _warnInput = Numeric(1, 99, current.WarnThreshold);
        _criticalInput = Numeric(2, 100, current.CriticalThreshold);
        _autostartCheck = new CheckBox { Content = "Start on login", IsChecked = current.AutostartEnabled };
        _updateCheck = new CheckBox { Content = "Check for updates automatically", IsChecked = current.CheckForUpdates };

        var general = Page(
            LabeledRow("Update frequency", _intervalCombo),
            Hint("Intervals under 3 min may be rate limited; the app backs off automatically."),
            LabeledRow("Warn threshold (%)", _warnInput),
            LabeledRow("Critical threshold (%)", _criticalInput),
            _autostartCheck,
            _updateCheck);

        // --- Alerts ---
        _warnAlerts = new CheckBox { Content = "Popup when a window hits the warn threshold", IsChecked = current.WarnAlertsEnabled };
        _criticalAlerts = new CheckBox { Content = "Popup when a window hits the critical threshold", IsChecked = current.CriticalAlertsEnabled };
        _maxedAlerts = new CheckBox { Content = "Popup when a window hits 100%", IsChecked = current.MaxedAlertsEnabled };
        _alertFiveHour = new CheckBox { Content = "5-hour session", IsChecked = current.AlertFiveHour };
        _alertSevenDay = new CheckBox { Content = "Weekly (all models)", IsChecked = current.AlertSevenDay };
        _alertOpus = new CheckBox { Content = "Weekly (Opus)", IsChecked = current.AlertSevenDayOpus };
        _alertSonnet = new CheckBox { Content = "Weekly (Sonnet)", IsChecked = current.AlertSevenDaySonnet };

        _placement = new ComboBox
        {
            ItemsSource = new[] { "Near tray", "Centered" },
            SelectedIndex = current.PopupPlacement == PopupPlacement.Centered ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = FieldWidth,
        };
        _autoDismissInput = Numeric(0, 300, current.PopupAutoDismissSeconds);
        _alertRepeatInput = Numeric(0, 1440, current.AlertRepeatMinutes);

        var windows = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Place(windows, _alertFiveHour, 0, 0);
        Place(windows, _alertSevenDay, 0, 1);
        Place(windows, _alertOpus, 1, 0);
        Place(windows, _alertSonnet, 1, 1);

        var alerts = Page(
            _warnAlerts,
            _criticalAlerts,
            _maxedAlerts,
            SectionLabel("Watch these windows"),
            windows,
            LabeledRow("Popup placement", _placement),
            LabeledRow("Auto-dismiss popups after (seconds, 0 = never)", _autoDismissInput),
            Hint("Centered popups (including every 100% popup) always wait for you to dismiss them."),
            LabeledRow("Re-notify every (minutes, 0 = only once until reset)", _alertRepeatInput));

        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "General", Content = general },
                new TabItem { Header = "Alerts", Content = alerts },
            },
        };
        var pages = new List<Control> { general, alerts };
        if (BackupTaskManager.WorkerAvailable())
        {
            var backup = BuildBackupPage();
            _tabs.Items.Add(new TabItem { Header = "Backup", Content = backup });
            pages.Add(backup);
        }

        // Every page as tall as the tallest, so the window does not jump in
        // size when switching tabs. Measured once the window is open: a page
        // only has its real (styled) size while it is the selected tab.
        Opened += (_, _) => EqualizePageHeights(pages.ToArray());

        _error = new TextBlock
        {
            Text = "Warn threshold must be lower than the critical threshold.",
            Foreground = Brushes.OrangeRed,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };

        var okButton = new Button { Content = "OK", IsDefault = true };
        okButton.Click += OnOk;
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };
        cancelButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                _tabs,
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton },
                },
            },
        };
    }

    private Control BuildBackupPage()
    {
        var config = BackupConfig.Load(_backupConfigPath);
        _schedule = config.Schedule;

        var help = new Button { Content = "Help" };
        help.Click += async (_, _) => await new BackupHelpDialog().ShowDialog(this);
        var advanced = new Button { Content = "Advanced..." };
        advanced.Click += async (_, _) => await OnOpenAdvancedAsync();

        _destinationsSummary = new ListBox { Height = 110 };
        var manage = new Button { Content = "Manage destinations..." };
        manage.Click += async (_, _) => await OnManageDestinationsAsync();
        RefreshDestinationsSummary();

        _backupFrequency = new ComboBox
        {
            ItemsSource = BackupFrequencies.Select(f => f.Display).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(BackupFrequencies, f => f.Value == config.Schedule.Frequency.ToLowerInvariant())),
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = FieldWidth,
        };
        _backupTime = new TextBox { Text = config.Schedule.Time, Width = FieldWidth, HorizontalAlignment = HorizontalAlignment.Right };

        var restore = new Button { Content = "Restore..." };
        restore.Click += async (_, _) => await OnOpenRestoreAsync();
        var saveSchedule = new Button { Content = "Save and register schedule" };
        saveSchedule.Click += async (_, _) => await OnSaveBackupScheduleAsync();
        var runNow = new Button { Content = "Back up now" };
        runNow.Click += async (_, _) =>
        {
            runNow.IsEnabled = false;
            try { await RunBackupNowAsync(); }
            finally { runNow.IsEnabled = true; }
        };

        var topButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { advanced, help },
        };

        return Page(
            topButtons,
            SectionLabel("Backup destinations"),
            DestinationTable.Header(),
            _destinationsSummary,
            manage,
            LabeledRow("Frequency", _backupFrequency),
            LabeledRow("Time (24h HH:mm)", _backupTime),
            new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { Spaced(restore), Spaced(saveSchedule), Spaced(runNow) },
            });

        static Control Spaced(Control c)
        {
            c.Margin = new Thickness(8, 0, 0, 6);
            return c;
        }
    }

    private void RefreshDestinationsSummary()
    {
        var config = BackupConfig.Load(_backupConfigPath);
        var status = BackupStatus.Load(_backupStatusPath);
        _destinationsSummary!.ItemsSource = config.Destinations.Select(d => DestinationTable.Row(d, status)).ToList();
    }

    private async Task OnManageDestinationsAsync()
    {
        var dialog = new BackupDestinationsDialog(_backupConfigPath, _backupStatusPath);
        await dialog.ShowDialog(this);
        RefreshDestinationsSummary();
        if (dialog.Changed)
            BackupDestinationsChanged?.Invoke();
    }

    private async Task OnOpenAdvancedAsync()
    {
        var dialog = new BackupAdvancedDialog(_schedule);
        if (!await dialog.ShowDialog<bool>(this))
            return;
        // Held here until "Save and register schedule", as on Windows.
        _schedule = new ScheduleConfig
        {
            Frequency = _schedule.Frequency,
            Time = _schedule.Time,
            StartWhenAvailable = dialog.StartWhenAvailable,
            RunOnlyIfNetworkAvailable = dialog.RunOnlyIfNetworkAvailable,
            DisallowStartIfOnBatteries = dialog.DisallowStartIfOnBatteries,
            StopIfGoingOnBatteries = dialog.StopIfGoingOnBatteries,
            RestartOnFailure = dialog.RestartOnFailure,
            RestartIntervalMinutes = dialog.RestartIntervalMinutes,
            RestartCount = dialog.RestartCount,
            BackupStaleAfterDays = dialog.BackupStaleAfterDays,
        };
    }

    private async Task OnOpenRestoreAsync()
    {
        var config = BackupConfig.Load(_backupConfigPath);
        if (!config.Destinations.Any(d => d.Enabled))
        {
            await MessageDialog.ShowAsync(this,
                "No backup destination is enabled. Add and enable one first, via " +
                "\"Manage destinations...\" on the Backup tab.");
            return;
        }
        await new RestoreDialog(config, new ProcessRunner()).ShowDialog(this);
    }

    private async Task RunBackupNowAsync()
    {
        var exitCode = await BackupTaskManager.RunNowAsync();
        RefreshDestinationsSummary();
        await MessageDialog.ShowAsync(this, BackupTaskManager.ResultMessage(exitCode), warning: exitCode != 0);
    }

    private async Task OnSaveBackupScheduleAsync()
    {
        var time = (_backupTime!.Text ?? "").Trim();
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            await MessageDialog.ShowAsync(this, "Time must be a 24-hour value in HH:mm format, e.g. 09:00.", warning: true);
            return;
        }

        var config = BackupConfig.Load(_backupConfigPath);
        config.Schedule.Frequency = BackupFrequencies[_backupFrequency!.SelectedIndex].Value;
        config.Schedule.Time = time;
        config.Schedule.StartWhenAvailable = _schedule.StartWhenAvailable;
        config.Schedule.RunOnlyIfNetworkAvailable = _schedule.RunOnlyIfNetworkAvailable;
        config.Schedule.DisallowStartIfOnBatteries = _schedule.DisallowStartIfOnBatteries;
        config.Schedule.StopIfGoingOnBatteries = _schedule.StopIfGoingOnBatteries;
        config.Schedule.RestartOnFailure = _schedule.RestartOnFailure;
        config.Schedule.RestartIntervalMinutes = _schedule.RestartIntervalMinutes;
        config.Schedule.RestartCount = _schedule.RestartCount;
        config.Schedule.BackupStaleAfterDays = _schedule.BackupStaleAfterDays;
        config.Save(_backupConfigPath);

        bool ok;
        string message;
        if (config.Destinations.Any(d => d.Enabled))
        {
            ok = await Task.Run(() => BackupTaskManager.Register(config.Schedule));
            message = ok
                ? "Backup settings saved and the schedule registered."
                : "Backup settings saved, but registering the schedule failed. See the log for details.";
        }
        else
        {
            var outcome = await Task.Run(BackupTaskManager.Unregister);
            ok = outcome != UnregisterOutcome.Failed;
            message = outcome switch
            {
                UnregisterOutcome.Removed => "Backup settings saved. No destination is enabled, so the schedule was removed.",
                UnregisterOutcome.NotFound => "Backup settings saved. No destination is enabled; there was no schedule to remove.",
                _ => "Backup settings saved, but removing the existing schedule failed. See the log for details.",
            };
        }
        await MessageDialog.ShowAsync(this, message, warning: !ok);
    }

    private void EqualizePageHeights(params Control[] pages)
    {
        var selected = _tabs.SelectedIndex;
        double tallest = 0;
        for (var i = 0; i < pages.Length; i++)
        {
            _tabs.SelectedIndex = i;
            UpdateLayout();
            tallest = Math.Max(tallest, pages[i].Bounds.Height);
        }
        foreach (var page in pages)
            page.MinHeight = tallest;
        _tabs.SelectedIndex = selected;
    }

    private static NumericUpDown Numeric(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        FormatString = "0",
        Width = FieldWidth,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static StackPanel Page(params Control[] children)
    {
        var page = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        page.Children.AddRange(children);
        return page;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static void Place(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static Control LabeledRow(string label, Control input)
    {
        DockPanel.SetDock(input, Dock.Right);
        return new DockPanel
        {
            Children =
            {
                input,
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 12, 0),
                },
            },
        };
    }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_warnInput.Value >= _criticalInput.Value)
        {
            // The thresholds live on General; make sure the error is next to
            // the fields it is about.
            _tabs.SelectedIndex = 0;
            _error.IsVisible = true;
            return;
        }
        Saved?.Invoke();
        Close();
    }

    /// <summary>Apply the current field values onto a settings object.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.PollIntervalMinutes = AppSettings.IntervalPresets[_intervalCombo.SelectedIndex];
        settings.WarnThreshold = (int)(_warnInput.Value ?? _warnDefault);
        settings.CriticalThreshold = (int)(_criticalInput.Value ?? _criticalDefault);
        settings.AutostartEnabled = _autostartCheck.IsChecked ?? false;
        settings.CheckForUpdates = _updateCheck.IsChecked ?? false;

        settings.WarnAlertsEnabled = _warnAlerts.IsChecked ?? false;
        settings.CriticalAlertsEnabled = _criticalAlerts.IsChecked ?? false;
        settings.MaxedAlertsEnabled = _maxedAlerts.IsChecked ?? false;
        settings.AlertFiveHour = _alertFiveHour.IsChecked ?? false;
        settings.AlertSevenDay = _alertSevenDay.IsChecked ?? false;
        settings.AlertSevenDayOpus = _alertOpus.IsChecked ?? false;
        settings.AlertSevenDaySonnet = _alertSonnet.IsChecked ?? false;
        settings.PopupPlacement = _placement.SelectedIndex == 1 ? PopupPlacement.Centered : PopupPlacement.NearTray;
        settings.PopupAutoDismissSeconds = (int)(_autoDismissInput.Value ?? 12);
        settings.AlertRepeatMinutes = (int)(_alertRepeatInput.Value ?? 0);
        // Normalize clamps the numbers and keeps the thresholds consistent,
        // exactly as on load.
        settings.Normalize();
    }
}

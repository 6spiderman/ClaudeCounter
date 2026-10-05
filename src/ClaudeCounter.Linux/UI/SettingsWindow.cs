using Avalonia.Controls;
using Avalonia.Layout;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public sealed class SettingsWindow : Window
{
    private readonly ComboBox _intervalCombo;
    private readonly NumericUpDown _warnInput;
    private readonly NumericUpDown _criticalInput;
    private readonly CheckBox _autostartCheck;
    private readonly CheckBox _updateCheck;
    private readonly TextBlock _error;
    private readonly int _warnDefault;
    private readonly int _criticalDefault;

    /// <summary>Raised once the user confirms; call <see cref="ApplyTo"/> to read the values.</summary>
    public event Action? Saved;

    public SettingsWindow(AppSettings current)
    {
        _warnDefault = current.WarnThreshold;
        _criticalDefault = current.CriticalThreshold;

        Title = "ClaudeCounter Settings";
        Width = 380;
        Height = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _intervalCombo = new ComboBox
        {
            ItemsSource = AppSettings.IntervalPresets.Select(m => $"{m} min").ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.IntervalPresets, current.PollIntervalMinutes)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = 130,
        };

        _warnInput = new NumericUpDown
        {
            Minimum = 1, Maximum = 99, Value = current.WarnThreshold, FormatString = "0",
            Width = 130, HorizontalAlignment = HorizontalAlignment.Right,
        };
        _criticalInput = new NumericUpDown
        {
            Minimum = 2, Maximum = 100, Value = current.CriticalThreshold, FormatString = "0",
            Width = 130, HorizontalAlignment = HorizontalAlignment.Right,
        };

        _autostartCheck = new CheckBox { Content = "Start on login", IsChecked = current.AutostartEnabled };
        _updateCheck = new CheckBox
        {
            Content = "Check for updates automatically",
            IsChecked = current.CheckForUpdates,
        };

        _error = new TextBlock
        {
            Text = "Warn threshold must be lower than the critical threshold.",
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            IsVisible = false,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var okButton = new Button { Content = "OK", IsDefault = true };
        okButton.Click += OnOk;
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };
        cancelButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10,
            Children =
            {
                LabeledRow("Update frequency", _intervalCombo),
                new TextBlock
                {
                    Text = "Intervals under 3 min may be rate limited; the app backs off automatically.",
                    Foreground = Avalonia.Media.Brushes.Gray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                LabeledRow("Warn threshold (%)", _warnInput),
                LabeledRow("Critical threshold (%)", _criticalInput),
                _autostartCheck,
                _updateCheck,
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

    private static Control LabeledRow(string label, Control input)
    {
        DockPanel.SetDock(input, Dock.Right);
        return new DockPanel
        {
            Children =
            {
                input,
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_warnInput.Value >= _criticalInput.Value)
        {
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
    }
}

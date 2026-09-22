using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeCounter.Core;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

/// <summary>
/// Shows the same usage summary as the Windows build's FlyoutForm. Linux
/// desktop environments do not reliably expose the cursor position or a tray
/// icon's screen rect to an application the way Windows does, so this opens
/// near the primary screen's top-right corner instead of anchored to the
/// click point.
/// </summary>
public sealed class FlyoutWindow : Window
{
    private const int Pad = 14;

    private readonly Func<AppSettings> _getSettings;
    private PollState? _state;
    private string? _updateVersion;

    public event Action? RefreshRequested;
    public event Action? UpdateRequested;
    public event Action? SignInRequested;

    public FlyoutWindow(Func<AppSettings> getSettings)
    {
        _getSettings = getSettings;

        Title = "ClaudeCounter";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Borderless, like the Windows build's FlyoutForm (FormBorderStyle.None):
        // this is a transient popup, not a window with its own close button.
        WindowDecorations = WindowDecorations.None;

        Deactivated += (_, _) => Hide();

        RebuildContent();
    }

    // Belt and braces alongside the borderless chrome above: some window
    // managers still offer a way to close an undecorated window (Alt+F4,
    // right-click in an alt-tab list). Closing this window for real would
    // leave it disposed, so the next tray-icon click could never show it
    // again - it must only ever be hidden until the app itself exits.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    public void UpdateState(PollState state)
    {
        _state = state;
        if (IsVisible)
            RebuildContent();
    }

    /// <summary>Adds a one-line "a newer release exists" notice to the flyout.</summary>
    public void ShowUpdateAvailable(string version)
    {
        _updateVersion = version;
        if (IsVisible)
            RebuildContent();
    }

    public void ShowNearTray()
    {
        RebuildContent();
        if (Screens.Primary is { } screen)
        {
            var wa = screen.WorkingArea;
            Position = new PixelPoint(wa.Right - (int)Width - 12, wa.Y + 12);
        }
        Show();
        Activate();
    }

    private void RebuildContent()
    {
        var settings = _getSettings();
        var panel = new StackPanel { Margin = new Thickness(Pad), Spacing = 4 };

        panel.Children.Add(new TextBlock
        {
            Text = "Claude Max usage",
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var snapshot = _state?.Snapshot;
        if (snapshot is not null)
        {
            var now = DateTimeOffset.UtcNow;
            AddUsageRow(panel, "5-hour session", snapshot.FiveHour, settings, now);
            AddUsageRow(panel, "Weekly (all models)", snapshot.SevenDay, settings, now);
            AddUsageRow(panel, "Weekly (Opus)", snapshot.SevenDayOpus, settings, now);
            AddUsageRow(panel, "Weekly (Sonnet)", snapshot.SevenDaySonnet, settings, now);

            if (snapshot.ExtraUsage is { IsEnabled: true } extra)
            {
                var used = extra.UsedCredits?.ToString("0.00") ?? "?";
                var limit = extra.MonthlyLimit?.ToString("0.##") ?? "?";
                var pct = extra.Utilization is { } u ? $" ({u:0}%)" : "";
                panel.Children.Add(new TextBlock { Text = $"Extra usage: ${used} / ${limit}{pct}" });
            }
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "No data yet", Foreground = Brushes.Gray });
        }

        var needsSignIn = _state?.Problem is ProblemKind.SignInRequired or ProblemKind.TokenExpired;

        if (_state is { Problem: not ProblemKind.None, ProblemMessage: { } problem })
        {
            var color = needsSignIn ? BandPalette.BandColor(Band.Amber) : BandPalette.BandColor(Band.Red);
            panel.Children.Add(new TextBlock
            {
                Text = problem, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (_state is { IsBootstrapped: true })
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Using Claude Code's session - sign in for your own.",
                Foreground = new SolidColorBrush(BandPalette.BandColor(Band.Amber)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (_updateVersion is { } newVersion)
        {
            var link = new Button
            {
                Content = $"ClaudeCounter v{newVersion} is available",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(BandPalette.BandColor(Band.Green)),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            link.Click += (_, _) => UpdateRequested?.Invoke();
            panel.Children.Add(link);
        }

        var updated = _state?.LastSuccessAt is { } last
            ? $"Updated {last.ToLocalTime():HH:mm}"
            : "Never updated";
        panel.Children.Add(new TextBlock
        {
            Text = updated, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (needsSignIn || _state is { IsBootstrapped: true })
        {
            var signInButton = new Button { Content = "Sign in" };
            signInButton.Click += (_, _) => SignInRequested?.Invoke();
            buttons.Children.Add(signInButton);
        }
        var refreshButton = new Button { Content = "Refresh" };
        refreshButton.Click += (_, _) => RefreshRequested?.Invoke();
        buttons.Children.Add(refreshButton);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private static void AddUsageRow(
        StackPanel panel, string label, UsageWindow? window, AppSettings settings, DateTimeOffset now)
    {
        if (window is null)
            return;

        var band = IconRenderer.BandFor(window.Utilization, settings);
        var color = new SolidColorBrush(BandPalette.BandColor(band));

        var header = new DockPanel();
        var percent = new TextBlock
        {
            Text = $"{window.Utilization:0}%", Foreground = color, FontWeight = FontWeight.SemiBold,
        };
        DockPanel.SetDock(percent, Dock.Right);
        header.Children.Add(percent);
        header.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(header);

        panel.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(window.Utilization, 0, 100),
            Height = 6,
            Foreground = color,
            Margin = new Thickness(0, 2, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = window.ResetsAt is { } resetsAt
                ? $"Resets in {TimeText.Countdown(resetsAt, now)}"
                : "No active limit",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        });
    }
}

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
/// in the primary screen's corner next to the panel instead of anchored to
/// the click point - see <see cref="PanelPlacement"/>.
/// </summary>
public sealed class FlyoutWindow : Window
{
    private const int Pad = 14;
    private const int ScreenMargin = 12;

    // How long after an auto-hide (focus loss) a tray click still counts as
    // "close the flyout" rather than "open it again" - see RecentlyDismissed.
    private static readonly TimeSpan DismissGrace = TimeSpan.FromMilliseconds(400);

    private readonly Func<AppSettings> _getSettings;
    private PollState? _state;
    private string? _updateVersion;
    private DateTime _autoHiddenAtUtc = DateTime.MinValue;

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

        Deactivated += (_, _) =>
        {
            if (!IsVisible)
                return;
            _autoHiddenAtUtc = DateTime.UtcNow;
            Hide();
        };

        RebuildContent();
    }

    /// <summary>
    /// True for a moment after the flyout hid itself on losing focus. Clicking
    /// the tray icon while the flyout is open moves focus to the panel first,
    /// so by the time the click arrives the flyout is already hidden - without
    /// this the click would immediately reopen it and the tray icon could
    /// never close the flyout.
    /// </summary>
    public bool RecentlyDismissed => DateTime.UtcNow - _autoHiddenAtUtc < DismissGrace;

    // Belt and braces alongside the borderless chrome above: some window
    // managers still offer a way to close an undecorated window (Alt+F4,
    // right-click in an alt-tab list). Closing this window for real would
    // leave it disposed, so the next tray-icon click could never show it
    // again - a user-initiated close only ever hides it.
    //
    // A close driven by logout/shutdown or by the app exiting must go
    // through, though: Avalonia's session-management handler (X11 XSMP)
    // closes every open window when the desktop asks to log out, and
    // cancels the whole logout if any window refuses. This window stays
    // registered (hidden) from the first time it is shown, so refusing here
    // made ClaudeCounter cancel every logout and shutdown for the rest of
    // the session once the flyout had been opened even once.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            base.OnClosing(e);
            return;
        }
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
            Position = PositionNearPanel(screen);
        Show();
        Activate();
    }

    private PixelPoint PositionNearPanel(Avalonia.Platform.Screen screen)
    {
        var (width, height) = PanelPlacement.MeasurePixels(Content as Control, Width, screen.Scaling, 300);
        return PanelPlacement.NearPanel(screen, width, height, (int)Math.Ceiling(ScreenMargin * screen.Scaling));
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

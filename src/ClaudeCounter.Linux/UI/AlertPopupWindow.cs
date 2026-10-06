using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeCounter.Notifications;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

/// <summary>
/// Linux counterpart of the Windows build's AlertPopupForm: a small card for a
/// usage-threshold crossing, with the same wording (<see cref="AlertContent"/>),
/// colours, tiers and dismissal rules.
/// </summary>
/// <remarks>
/// <para>
/// Near-tray popups sit in the panel corner (see <see cref="PanelPlacement"/>),
/// stack away from the panel when several fire at once, do not take focus,
/// and auto-dismiss after the configured number of seconds (0 = never).
/// Centered popups - every 100% popup, and any popup when placement is set
/// to Centered - take focus and wait for Dismiss or Esc.
/// </para>
/// <para>
/// Never cancels its own close: a window that refused would make Avalonia
/// cancel the desktop's logout (see FlyoutWindow.OnClosing).
/// </para>
/// </remarks>
public sealed class AlertPopupWindow : Window
{
    private const double PopupWidth = 300;
    private const int StackGap = 8;
    private const int ScreenMargin = 12;

    // Open near-tray popups, oldest first, so a new one stacks beyond them.
    // Only touched on the UI thread.
    private static readonly List<AlertPopupWindow> NearTrayPopups = new();

    private readonly bool _centered;
    private DispatcherTimer? _dismissTimer;

    private AlertPopupWindow(
        string title, string body, double titleFontSize,
        Color back, Color fore, bool centered, int autoDismissSeconds)
    {
        _centered = centered;

        Title = "ClaudeCounter alert";
        Width = PopupWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        // Near-tray popups must not pull focus away from whatever the user is
        // typing in; a centered popup is the "stop and look" tier.
        ShowActivated = centered;
        Background = new SolidColorBrush(back);

        Content = BuildContent(title, body, titleFontSize, fore);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };

        if (!centered && autoDismissSeconds > 0)
        {
            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoDismissSeconds) };
            _dismissTimer.Tick += (_, _) => Close();
        }

        Closed += (_, _) =>
        {
            _dismissTimer?.Stop();
            // Let the remaining popups close the gap.
            if (NearTrayPopups.Remove(this))
                Restack();
        };
    }

    /// <summary>
    /// Show a popup for one alert event. Call on the UI thread.
    /// <paramref name="autoDismissSeconds"/> only governs near-tray popups;
    /// 0 means they never auto-dismiss.
    /// </summary>
    public static void Show(AlertEvent e, PopupPlacement placement, int autoDismissSeconds)
    {
        // 100% always takes the centre and focus; the other levels follow the setting.
        var centered = placement == PopupPlacement.Centered || e.Level == AlertLevel.Maxed;
        var (back, fore) = ColorsFor(e.Level);
        var titleFontSize = e.Level == AlertLevel.Maxed ? 18 : 14;
        var (title, body) = AlertContent.For(e, DateTimeOffset.Now);

        var popup = new AlertPopupWindow(title, body, titleFontSize, back, fore, centered, autoDismissSeconds);
        popup.ShowPlaced();
    }

    /// <summary>
    /// Same accents as the Windows popup: red for Maxed and Critical, amber
    /// for Warn. Critical and Warn use black text - on this red, white text
    /// only reaches ~3.4:1 contrast, below the 4.5:1 normal text needs - and
    /// Maxed keeps white, where its larger title carries the message.
    /// </summary>
    public static (Color Back, Color Fore) ColorsFor(AlertLevel level) => level switch
    {
        AlertLevel.Maxed => (BandPalette.BandColor(Band.Red), Colors.White),
        AlertLevel.Critical => (BandPalette.BandColor(Band.Red), Colors.Black),
        AlertLevel.Warn => (BandPalette.BandColor(Band.Amber), Colors.Black),
        _ => (BandPalette.BandColor(Band.Gray), Colors.White),
    };

    private void ShowPlaced()
    {
        if (_centered)
        {
            if (Screens.Primary is { } screen)
            {
                var (width, height) = PanelPlacement.MeasurePixels(Content as Control, PopupWidth, screen.Scaling, 120);
                Position = PanelPlacement.Centered(screen, width, height);
            }
            // Re-centre on the real size once laid out.
            Opened += (_, _) =>
            {
                if (Screens.Primary is { } s)
                    Position = PanelPlacement.Centered(s, PixelWidth(s), PixelHeight(s));
            };
            Show();
            Activate();
            return;
        }

        NearTrayPopups.Add(this);
        // A first placement from the pre-show measurement, then a precise one
        // from the real sizes once this popup has been laid out: a control
        // measured before it is shown has no template yet, so it comes out a
        // few pixels short, which is enough to make stacked popups touch.
        Restack();
        Opened += (_, _) => Restack();
        Show();
        _dismissTimer?.Start();
    }

    /// <summary>
    /// Lays the open near-tray popups out from the panel corner outwards,
    /// oldest nearest the panel, with a gap between each.
    /// </summary>
    private static void Restack()
    {
        var offset = 0;
        foreach (var popup in NearTrayPopups)
        {
            if (popup.Screens.Primary is not { } screen)
                continue;
            var width = popup.PixelWidth(screen);
            var height = popup.PixelHeight(screen);
            popup.Position = PanelPlacement.NearPanel(
                screen, width, height, (int)Math.Ceiling(ScreenMargin * screen.Scaling), offset);
            offset += height + (int)Math.Ceiling(StackGap * screen.Scaling);
        }
    }

    private int PixelWidth(Avalonia.Platform.Screen screen) => (int)Math.Ceiling(PopupWidth * screen.Scaling);

    // The real height once shown; before that, the pre-show measurement.
    private int PixelHeight(Avalonia.Platform.Screen screen) =>
        IsVisible && ClientSize.Height > 0
            ? (int)Math.Ceiling(ClientSize.Height * screen.Scaling)
            : PanelPlacement.MeasurePixels(Content as Control, PopupWidth, screen.Scaling, 120).Height;

    private Control BuildContent(string title, string body, double titleFontSize, Color fore)
    {
        var foreground = new SolidColorBrush(fore);

        var dismiss = new Button
        {
            Content = "Dismiss",
            Foreground = foreground,
            // A darkened overlay of the card's own accent, like the Windows
            // popup's Dismiss button, rather than an unrelated grey patch.
            Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 4),
            Margin = new Thickness(0, 6, 0, 0),
        };
        dismiss.Click += (_, _) => Close();

        return new Border
        {
            // A thin darker edge, since a borderless window has no frame of
            // its own to separate it from whatever is behind it.
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(14),
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = titleFontSize,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = foreground,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = body,
                        Foreground = foreground,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    dismiss,
                },
            },
        };
    }
}

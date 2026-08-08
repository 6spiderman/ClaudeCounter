using ClaudeCounter.Core;
using ClaudeCounter.Notifications;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public static class AlertContent
{
    public static (string Title, string Body) For(AlertEvent e, DateTimeOffset now)
    {
        var reset = e.ResetsAt is { } at ? $", resets in {TimeText.Countdown(at, now)}" : "";
        if (e.Level == AlertLevel.Maxed)
            return ("Time to touch some grass",
                    $"{e.WindowLabel} is at 100%{reset}.");
        return ($"{e.WindowLabel} usage critical",
                $"At {e.Utilization:0}%{reset}.");
    }
}

public sealed class AlertPopupForm : Form
{
    private const int Width_ = 300;
    private static readonly TimeSpan AutoDismiss = TimeSpan.FromSeconds(12);

    private readonly bool _noActivate;
    private readonly System.Windows.Forms.Timer? _dismissTimer;

    private AlertPopupForm(AlertEvent e, PopupPlacement placement, Point trayAnchor)
    {
        // 100% always takes the center + focus; critical follows the setting.
        var centered = placement == PopupPlacement.Centered || e.Level == AlertLevel.Maxed;
        _noActivate = !centered;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Width = Width_;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.Manual;

        var palette = Theme.Current();
        BackColor = e.Level == AlertLevel.Maxed
            ? Theme.BandColor(Band.Red) : palette.Back;

        var (title, body) = AlertContent.For(e, DateTimeOffset.Now);
        BuildContent(title, body, e.Level, palette);
        Place(centered, trayAnchor);

        if (_noActivate)
        {
            _dismissTimer = new System.Windows.Forms.Timer { Interval = (int)AutoDismiss.TotalMilliseconds };
            _dismissTimer.Tick += (_, _) => Close();
            _dismissTimer.Start();
        }
    }

    /// <summary>Show a popup for one alert event. Call on the UI thread.</summary>
    public static void Show(AlertEvent e, PopupPlacement placement, Point trayAnchor)
    {
        var form = new AlertPopupForm(e, placement, trayAnchor);
        if (form._noActivate)
            form.Show();      // ShowWithoutActivation keeps focus with the active app
        else
        {
            form.Show();
            form.Activate();
        }
    }

    // Do not steal focus for near-tray popups.
    protected override bool ShowWithoutActivation => _noActivate;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            if (_noActivate)
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.TryRoundCorners(Handle);
    }

    private void BuildContent(string title, string body, AlertLevel level, Palette palette)
    {
        var fore = level == AlertLevel.Maxed ? Color.White : palette.Fore;
        var pad = 14;
        var titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", level == AlertLevel.Maxed ? 13f : 10.5f),
            ForeColor = fore, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(Width - pad * 2, 0), Location = new Point(pad, pad),
        };
        Controls.Add(titleLabel);

        var bodyLabel = new Label
        {
            Text = body, ForeColor = fore, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(Width - pad * 2, 0),
            Location = new Point(pad, titleLabel.Bottom + 6),
        };
        Controls.Add(bodyLabel);

        var dismiss = new Button
        {
            Text = "Dismiss", FlatStyle = FlatStyle.Flat, ForeColor = fore,
            BackColor = level == AlertLevel.Maxed ? Color.FromArgb(60, 0, 0, 0) : palette.BarBack,
            Size = new Size(80, 28),
            Location = new Point(Width - pad - 80, bodyLabel.Bottom + 10),
        };
        dismiss.Click += (_, _) => Close();
        Controls.Add(dismiss);
        Height = dismiss.Bottom + pad;
    }

    private void Place(bool centered, Point trayAnchor)
    {
        var area = Screen.FromPoint(trayAnchor).WorkingArea;
        if (centered)
        {
            var full = Screen.FromPoint(trayAnchor).Bounds;
            Location = new Point(full.Left + (full.Width - Width) / 2,
                                 full.Top + (full.Height - Height) / 2);
        }
        else
        {
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dismissTimer?.Dispose();
        base.Dispose(disposing);
    }
}

using ClaudeBackup;
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
        if (e.Level == AlertLevel.Warn)
            return ($"{e.WindowLabel} usage warning",
                    $"At {e.Utilization:0}%{reset}.");
        return ($"{e.WindowLabel} usage critical",
                $"At {e.Utilization:0}%{reset}.");
    }
}

/// <summary>
/// A small themed popup card, shown either for a usage-threshold crossing
/// (<see cref="Show"/>) or (S11b) for a transition into a backup-health
/// problem state (<see cref="ShowBackupHealth"/>). Both factories funnel into
/// the same private constructor/<see cref="ShowForm"/> helper, so placement,
/// stacking, auto-dismiss, Esc-to-close and rounded-corner chrome are all
/// genuinely shared code, not two parallel implementations - "reusing the
/// existing alert popup infrastructure so the user's placement and
/// auto-dismiss settings are honoured" (design spec) is what this refactor
/// exists to guarantee structurally rather than by convention.
/// </summary>
public sealed class AlertPopupForm : Form
{
    private const int PopupWidth = 300;
    private const int StackGap = 8;

    // Near-tray popups only, tracked so a later popup in the same poll stacks
    // above earlier ones instead of drawing on top of them. All access happens
    // on the UI thread (construction, Place, and OnFormClosed all run there),
    // so a plain List<> is fine - no locking needed.
    private static readonly List<AlertPopupForm> NearTrayPopups = new();

    private readonly bool _noActivate;
    private readonly System.Windows.Forms.Timer? _dismissTimer;

    private AlertPopupForm(
        string title, string body, float titleFontSize,
        Color backColor, Color foreColor, Color dismissBackColor,
        bool centered, Point anchor, int autoDismissSeconds)
    {
        _noActivate = !centered;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Width = PopupWidth;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        BackColor = backColor;
        BuildContent(title, body, titleFontSize, foreColor, dismissBackColor);
        Place(centered, anchor);

        // Esc-to-close as a safety valve: the centered/Maxed popup is
        // chrome-less, TopMost and hidden from Alt+Tab (WS_EX_TOOLWINDOW), with
        // no auto-dismiss timer - Dismiss is otherwise the only way out.
        KeyDown += (_, ev) =>
        {
            if (ev.KeyCode == Keys.Escape)
                Close();
        };

        if (_noActivate)
        {
            // Reserve our height for whichever near-tray popup comes next.
            NearTrayPopups.Add(this);

            // 0 means "never auto-dismiss" - create no timer at all rather
            // than one that never fires, so there is nothing to Stop/Dispose
            // that could ever matter and no Tick handler that could ever run.
            if (autoDismissSeconds > 0)
            {
                _dismissTimer = new System.Windows.Forms.Timer { Interval = autoDismissSeconds * 1000 };
                _dismissTimer.Tick += (_, _) => Close();
                _dismissTimer.Start();
            }
        }
    }

    /// <summary>
    /// Show a popup for one alert event. Call on the UI thread.
    /// <paramref name="autoDismissSeconds"/> only governs near-tray popups - a
    /// centered popup (every Maxed popup is always centered, regardless of
    /// <paramref name="placement"/>) takes focus and always waits for
    /// explicit dismissal. 0 means the near-tray popup never auto-dismisses.
    /// </summary>
    public static void Show(AlertEvent e, PopupPlacement placement, Point anchor, int autoDismissSeconds)
    {
        // 100% always takes the center + focus; critical follows the setting.
        var centered = placement == PopupPlacement.Centered || e.Level == AlertLevel.Maxed;
        var (backColor, foreColor, dismissBackColor) = ColorsFor(e.Level, Theme.Current());
        var titleFontSize = e.Level == AlertLevel.Maxed ? 13f : 10.5f;

        var (title, body) = AlertContent.For(e, DateTimeOffset.Now);
        ShowForm(title, body, titleFontSize, backColor, foreColor, dismissBackColor, centered, anchor, autoDismissSeconds);
    }

    /// <summary>
    /// Pure color selection for a level's popup card, extracted so it is
    /// unit-testable without constructing a Form (same reasoning as
    /// <see cref="StackOffsetFor"/>).
    ///
    /// Maxed's red, Critical's (same) red, and Warn's amber are all fixed
    /// accent colors, not part of the light/dark theme pair, so they get
    /// their own fixed contrasting foreground rather than palette.Fore
    /// (which is tuned for palette.Back, not for an accent-colored card).
    /// Critical and Maxed deliberately share the same red band - their copy
    /// already differentiates urgency ("touch some grass" at 100% vs. the
    /// plain percentage at Critical) - but Critical uses Black text rather
    /// than Maxed's existing White: measured against
    /// Theme.BandColor(Band.Red) = (248, 81, 73), WCAG contrast is ~6.3:1
    /// for black vs. only ~3.4:1 for white, and 3.4:1 fails the 4.5:1 AA
    /// threshold this popup's normal-weight body text needs. Warn's amber
    /// is brighter still, so black reads even better there.
    ///
    /// Maxed, Critical and Warn all sit on a solid accent color rather than
    /// the neutral theme background, so their Dismiss button is a darkened
    /// overlay of that same accent rather than palette.BarBack (which would
    /// look like an unrelated gray patch dropped on top of it).
    /// </summary>
    public static (Color Back, Color Fore, Color DismissBack) ColorsFor(AlertLevel level, Palette palette)
    {
        var (back, fore) = level switch
        {
            AlertLevel.Maxed => (Theme.BandColor(Band.Red), Color.White),
            AlertLevel.Critical => (Theme.BandColor(Band.Red), Color.Black),
            AlertLevel.Warn => (Theme.BandColor(Band.Amber), Color.Black),
            _ => (palette.Back, palette.Fore),
        };
        var dismissBack = level is AlertLevel.Maxed or AlertLevel.Critical or AlertLevel.Warn
            ? Color.FromArgb(60, 0, 0, 0)
            : palette.BarBack;
        return (back, fore, dismissBack);
    }

    /// <summary>
    /// S11b: show a popup for one transition into a backup-health problem
    /// state. The caller (TrayApplicationContext) only invokes this on an
    /// actual transition - see BackupHealthPresenter.ShouldNotify - never on
    /// every poll, so no dedupe logic lives here. A backup problem is not the
    /// "you are at 100%" emergency tier, so unlike Maxed it always follows
    /// <paramref name="placement"/> rather than forcing centered
    /// focus-stealing; otherwise it reuses exactly the same placement,
    /// auto-dismiss, stacking, dismiss button and Esc-to-close behaviour as
    /// <see cref="Show"/>.
    /// </summary>
    public static void ShowBackupHealth(BackupHealthResult result, PopupPlacement placement, Point anchor, int autoDismissSeconds)
    {
        var centered = placement == PopupPlacement.Centered;
        var backColor = Theme.BandColor(Band.Amber);
        var (title, body) = BackupHealthPresenter.PopupContent(result, DateTimeOffset.Now);
        ShowForm(title, body, 10.5f, backColor, Color.Black, Color.FromArgb(60, 0, 0, 0), centered, anchor, autoDismissSeconds);
    }

    private static void ShowForm(
        string title, string body, float titleFontSize,
        Color backColor, Color foreColor, Color dismissBackColor,
        bool centered, Point anchor, int autoDismissSeconds)
    {
        var form = new AlertPopupForm(title, body, titleFontSize, backColor, foreColor, dismissBackColor, centered, anchor, autoDismissSeconds);
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Covers both dismissal paths (auto-dismiss timer and the Dismiss
        // button click) since both just call Close().
        NearTrayPopups.Remove(this);
        base.OnFormClosed(e);
    }

    private void BuildContent(string title, string body, float titleFontSize, Color foreColor, Color dismissBackColor)
    {
        var pad = 14;
        var titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", titleFontSize),
            ForeColor = foreColor, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(Width - pad * 2, 0), Location = new Point(pad, pad),
        };
        Controls.Add(titleLabel);

        var bodyLabel = new Label
        {
            Text = body, ForeColor = foreColor, BackColor = Color.Transparent, AutoSize = true,
            MaximumSize = new Size(Width - pad * 2, 0),
            Location = new Point(pad, titleLabel.Bottom + 6),
        };
        Controls.Add(bodyLabel);

        var dismiss = new Button
        {
            Text = "Dismiss", FlatStyle = FlatStyle.Flat, ForeColor = foreColor,
            BackColor = dismissBackColor,
            Size = new Size(80, 28),
            Location = new Point(Width - pad - 80, bodyLabel.Bottom + 10),
        };
        dismiss.Click += (_, _) => Close();
        Controls.Add(dismiss);
        Height = dismiss.Bottom + pad;
    }

    private void Place(bool centered, Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        if (centered)
        {
            var full = screen.Bounds;
            Location = new Point(full.Left + (full.Width - Width) / 2,
                                 full.Top + (full.Height - Height) / 2);
            return;
        }

        var area = screen.WorkingArea;
        var stackOffset = StackOffsetFor(NearTrayPopups.Select(p => p.Height).ToArray(), StackGap);
        var y = Math.Max(area.Top, area.Bottom - Height - 12 - stackOffset);
        Location = new Point(area.Right - Width - 12, y);
    }

    /// <summary>
    /// How far up (in pixels) a new near-tray popup must sit to clear the
    /// popups already stacked above the bottom-right corner. Pure and
    /// side-effect free so it is unit-testable without a Form.
    /// </summary>
    public static int StackOffsetFor(IReadOnlyList<int> openHeights, int gap)
    {
        var offset = 0;
        foreach (var height in openHeights)
            offset += height + gap;
        return offset;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dismissTimer?.Dispose();
        base.Dispose(disposing);
    }
}

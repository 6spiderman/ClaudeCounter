using Avalonia;
using Avalonia.Platform;

namespace ClaudeCounter.UI;

/// <summary>
/// Where a tray-related window goes on screen. Linux desktops do not tell an
/// app where its tray icon is, so windows that belong "near the tray" (the
/// flyout, near-tray alert popups) go in the work-area corner next to the
/// panel instead. The panel is whatever the work area excludes - KDE's
/// default bottom panel shrinks its bottom edge, GNOME's top bar its top edge
/// - so whichever edge moved in is where the tray lives. Defaults to the
/// right side, then the bottom, when nothing is excluded (an auto-hiding
/// panel, a bare window manager). All values are physical pixels.
/// </summary>
public static class PanelPlacement
{
    /// <summary>
    /// Top-left corner for a window of <paramref name="width"/> x
    /// <paramref name="height"/> pixels in the panel corner,
    /// <paramref name="stackOffset"/> pixels further away from the panel (so
    /// several popups can stack without overlapping).
    /// </summary>
    public static PixelPoint NearPanel(Screen screen, int width, int height, int margin, int stackOffset = 0)
    {
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;

        var panelLeft = work.X > bounds.X;
        var panelTop = work.Y > bounds.Y && work.Bottom >= bounds.Bottom;

        var x = panelLeft ? work.X + margin : work.Right - width - margin;
        var y = panelTop
            ? work.Y + margin + stackOffset
            : work.Bottom - height - margin - stackOffset;

        return new PixelPoint(
            Math.Clamp(x, work.X, Math.Max(work.X, work.Right - width)),
            Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height)));
    }

    /// <summary>Top-left corner that centers the window on the whole screen.</summary>
    public static PixelPoint Centered(Screen screen, int width, int height)
    {
        var bounds = screen.Bounds;
        return new PixelPoint(
            bounds.X + Math.Max(0, (bounds.Width - width) / 2),
            bounds.Y + Math.Max(0, (bounds.Height - height) / 2));
    }

    /// <summary>
    /// Pixel size of a borderless, fixed-width window whose height follows its
    /// content, measured before it is shown - showing first and moving
    /// afterwards visibly flickers.
    /// </summary>
    public static (int Width, int Height) MeasurePixels(Avalonia.Controls.Control? content, double width, double scaling, double fallbackHeight)
    {
        content?.Measure(new Size(width, double.PositiveInfinity));
        var height = content?.DesiredSize.Height ?? fallbackHeight;
        return ((int)Math.Ceiling(width * scaling), (int)Math.Ceiling(height * scaling));
    }
}

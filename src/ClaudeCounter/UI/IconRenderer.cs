using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ClaudeCounter.Core;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public static class IconRenderer
{
    // The thresholds live in Core (UsageBands) so the tray, `claudecounter
    // --status` and the Plasma widget always agree on the colour.
    public static Band BandFor(double utilization, AppSettings settings) =>
        UsageBands.For(utilization, settings.WarnThreshold, settings.CriticalThreshold) switch
        {
            UsageBand.Red => Band.Red,
            UsageBand.Amber => Band.Amber,
            UsageBand.Gray => Band.Gray,
            _ => Band.Green,
        };

    /// <summary>
    /// Renders a 32x32 tray icon: rounded square in the band color with the
    /// percentage (or "--") in white, plus (S11b) an optional small "problem"
    /// badge in the top-right corner when <paramref name="badge"/> is true -
    /// the caller passes <c>BackupHealthResult.State.WarrantsAttention()</c>,
    /// never a hand-rolled Failed-or-Stale check of its own. Caller owns the
    /// returned Icon and must dispose the previously displayed one after
    /// swapping.
    /// </summary>
    public static Icon Render(string text, Band band, bool badge = false)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Theme.BandColor(band));
            using var path = RoundedRect(new Rectangle(0, 0, size - 1, size - 1), 8);
            g.FillPath(background, path);

            var fontSize = text.Length switch
            {
                <= 2 => 13f,
                3 => 10f,
                _ => 8f,
            };
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size + 1), format);

            if (badge)
                DrawBadge(g, size);
        }

        // GDI handle hygiene: GetHicon allocates an unmanaged HICON that
        // Icon.FromHandle does NOT own. Clone to a managed icon, then destroy
        // the original handle, or the process leaks GDI objects every refresh.
        var hIcon = bitmap.GetHicon();
        try
        {
            using var unowned = Icon.FromHandle(hIcon);
            return (Icon)unowned.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// A small white-ringed red dot in the top-right corner, sized (roughly a
    /// third of the icon) to stay a clearly visible dot rather than a
    /// near-invisible speck once Windows downsamples this 32x32 source
    /// bitmap to the 16x16 the tray strip actually shows - the requirement
    /// this exists to satisfy is that the marker is legible at both sizes.
    /// Deliberately a fixed style regardless of which backup health
    /// sub-state triggered it (NeverRun/Failed/Stale all draw the same badge -
    /// the tooltip/flyout carry which one it is), and deliberately confined
    /// to the extreme corner so it does not obscure the centered percentage
    /// text this icon exists to keep readable.
    /// </summary>
    private static void DrawBadge(Graphics g, int size)
    {
        var diameter = size * 0.3125f; // 10px at the 32px size this renders
        var ringRect = new RectangleF(size - diameter - 1, 1, diameter, diameter);
        using var ring = new SolidBrush(Color.White);
        g.FillEllipse(ring, ringRect);

        var dotInset = diameter * 0.15f;
        var dotRect = RectangleF.Inflate(ringRect, -dotInset, -dotInset);
        using var dot = new SolidBrush(Color.FromArgb(237, 28, 36));
        g.FillEllipse(dot, dotRect);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

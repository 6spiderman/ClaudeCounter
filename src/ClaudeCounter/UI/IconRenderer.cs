using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public static class IconRenderer
{
    public static Band BandFor(double utilization, AppSettings settings) =>
        utilization >= settings.CriticalThreshold ? Band.Red
        : utilization >= settings.WarnThreshold ? Band.Amber
        : Band.Green;

    /// <summary>
    /// Renders a 32x32 tray icon: rounded square in the band color with the
    /// percentage (or "--") in white. Caller owns the returned Icon and must
    /// dispose the previously displayed one after swapping.
    /// </summary>
    public static Icon Render(string text, Band band)
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

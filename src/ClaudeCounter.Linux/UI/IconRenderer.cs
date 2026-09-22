using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

public static class IconRenderer
{
    private const int Size = 32;

    public static Band BandFor(double utilization, AppSettings settings) =>
        utilization >= settings.CriticalThreshold ? Band.Red
        : utilization >= settings.WarnThreshold ? Band.Amber
        : Band.Green;

    /// <summary>
    /// Renders a 32x32 tray icon: rounded square in the band color with the
    /// percentage (or "--") in white. Caller owns the returned WindowIcon.
    /// </summary>
    public static WindowIcon Render(string text, Band band)
    {
        var visual = new TrayIconVisual(text, band) { Width = Size, Height = Size };
        visual.Measure(new Size(Size, Size));
        visual.Arrange(new Rect(0, 0, Size, Size));

        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        bitmap.Render(visual);
        return new WindowIcon(bitmap);
    }

    private sealed class TrayIconVisual(string text, Band band) : Control
    {
        public override void Render(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(BandPalette.BandColor(band)), null,
                new Rect(0, 0, Size, Size), 8, 8);

            var fontSize = text.Length switch
            {
                <= 2 => 13,
                3 => 10,
                _ => 8,
            };
            var formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Sans Serif", weight: FontWeight.Bold),
                fontSize,
                Brushes.White);

            context.DrawText(formatted, new Point(
                (Size - formatted.Width) / 2,
                (Size - formatted.Height) / 2));
        }
    }
}

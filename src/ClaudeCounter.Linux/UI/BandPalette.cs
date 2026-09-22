using Avalonia.Media;

namespace ClaudeCounter.UI;

public enum Band
{
    Green,
    Amber,
    Red,
    Gray,
}

public static class BandPalette
{
    public static Color BandColor(Band band) => band switch
    {
        Band.Green => Color.FromRgb(46, 160, 67),
        Band.Amber => Color.FromRgb(210, 153, 34),
        Band.Red => Color.FromRgb(248, 81, 73),
        _ => Color.FromRgb(110, 118, 129),
    };
}

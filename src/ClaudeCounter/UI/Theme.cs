using Microsoft.Win32;

namespace ClaudeCounter.UI;

public sealed record Palette(
    Color Back,
    Color Fore,
    Color SubtleFore,
    Color BarBack,
    Color Border);

public enum Band
{
    Green,
    Amber,
    Red,
    Gray,
}

public static class Theme
{
    private static readonly Palette Dark = new(
        Back: Color.FromArgb(32, 32, 32),
        Fore: Color.FromArgb(229, 229, 229),
        SubtleFore: Color.FromArgb(150, 150, 150),
        BarBack: Color.FromArgb(58, 58, 58),
        Border: Color.FromArgb(70, 70, 70));

    private static readonly Palette Light = new(
        Back: Color.FromArgb(243, 243, 243),
        Fore: Color.FromArgb(26, 26, 26),
        SubtleFore: Color.FromArgb(110, 110, 110),
        BarBack: Color.FromArgb(214, 214, 214),
        Border: Color.FromArgb(190, 190, 190));

    public static Palette Current() => IsLightTheme() ? Light : Dark;

    public static Color BandColor(Band band) => band switch
    {
        Band.Green => Color.FromArgb(46, 160, 67),
        Band.Amber => Color.FromArgb(210, 153, 34),
        Band.Red => Color.FromArgb(248, 81, 73),
        _ => Color.FromArgb(110, 118, 129),
    };

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

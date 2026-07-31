using System.Runtime.InteropServices;

namespace ClaudeCounter;

internal static class NativeMethods
{
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Win11 rounded corners for borderless windows; harmless no-op elsewhere.</summary>
    public static void TryRoundCorners(IntPtr hwnd)
    {
        try
        {
            var preference = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }
}

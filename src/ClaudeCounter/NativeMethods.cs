using System.Runtime.InteropServices;

namespace ClaudeCounter;

internal static class NativeMethods
{
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    private const int STD_OUTPUT_HANDLE = -11;
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// ClaudeCounter.exe is a GUI app, so Windows gives it no console. For
    /// <c>--status</c> and friends: when the output is already redirected (a
    /// pipe, a file, Claude Code's status line) the inherited handle works as
    /// is; when it is not (typed into cmd or PowerShell), borrow the parent's
    /// console so the text shows up there. Must run before anything touches
    /// <see cref="Console"/>, which caches its handles on first use.
    /// </summary>
    public static void AttachToParentConsoleIfNeeded()
    {
        try
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                _ = AttachConsole(ATTACH_PARENT_PROCESS);
        }
        catch (Exception)
        {
            // No console to write to - the command still runs, just silently.
        }
    }

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

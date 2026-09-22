using ClaudeCounter.Core;

namespace ClaudeCounter.Settings;

/// <summary>
/// XDG autostart equivalent of the Windows Run-key manager: a
/// freedesktop.org <c>.desktop</c> file in <c>~/.config/autostart/</c>, which
/// every major desktop environment (GNOME, KDE, XFCE, ...) honours on login.
/// </summary>
public static class AutostartManager
{
    private const string DesktopFileName = "claudecounter.desktop";

    private static string DesktopFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "autostart", DesktopFileName);

    // Environment.ProcessPath is correct under single-file publish, unlike
    // Assembly.Location which is empty there.
    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine executable path");

    public static void Enable()
    {
        try
        {
            var directory = Path.GetDirectoryName(DesktopFilePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(DesktopFilePath, DesktopFileContent(ExePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not enable autostart: {e.Message}");
        }
    }

    public static void Disable()
    {
        try
        {
            File.Delete(DesktopFilePath); // no-op when absent
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not disable autostart: {e.Message}");
        }
    }

    public static bool IsEnabled() => File.Exists(DesktopFilePath);

    /// <summary>Self-heal the registered path if the exe was moved.</summary>
    public static void EnsurePathCurrent()
    {
        if (!IsEnabled())
            return;
        try
        {
            var current = File.ReadAllText(DesktopFilePath);
            var expected = DesktopFileContent(ExePath);
            if (current != expected)
                File.WriteAllText(DesktopFilePath, expected);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not update the autostart entry: {e.Message}");
        }
    }

    private static string DesktopFileContent(string exePath) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=ClaudeCounter\n" +
        "Comment=Claude plan usage in the system tray\n" +
        $"Exec=\"{exePath}\"\n" +
        "Terminal=false\n" +
        "Hidden=false\n" +
        "X-GNOME-Autostart-enabled=true\n";
}

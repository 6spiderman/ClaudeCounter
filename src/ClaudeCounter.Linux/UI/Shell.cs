using System.ComponentModel;
using System.Diagnostics;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// Hands things off to the desktop's default handlers via <c>xdg-open</c>.
/// Every call is best-effort: a machine with no browser association, or a
/// minimal window manager, must not take the tray app down.
/// </summary>
public static class Shell
{
    /// <summary>
    /// Opens a URL in the default browser. False when nothing handled it,
    /// including when the URL is refused.
    /// </summary>
    /// <remarks>
    /// Same https-only restriction as the Windows build: every caller in this
    /// app is expected to pass a genuine https URL, and refusing anything
    /// else here is the last line of defense in case a value from an external
    /// source (an update feed, a pasted string) ever reaches this call
    /// unsanitized.
    /// </remarks>
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warn("Refused to open a non-https URL.");
            return false;
        }

        return TryStart("xdg-open", uri.AbsoluteUri);
    }

    /// <summary>Opens the file manager on the log folder (xdg-open has no "select" equivalent across desktops).</summary>
    public static void ShowLogFolder()
    {
        if (Log.FilePath is not { } path)
        {
            Log.Warn("No log file has been created yet.");
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            return;

        TryStart("xdg-open", directory);
    }

    private static bool TryStart(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
            });
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            Log.Warn($"Could not run '{fileName}': {e.Message}");
            return false;
        }
    }
}

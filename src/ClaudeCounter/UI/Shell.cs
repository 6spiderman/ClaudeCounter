using System.Diagnostics;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// Hands things off to Explorer and the default browser. Every call is
/// best-effort: a machine with no browser association, or a locked-down shell,
/// must not take the tray app down.
/// </summary>
public static class Shell
{
    /// <summary>
    /// Opens a URL in the default browser. False when nothing handled it,
    /// including when the URL is refused.
    /// </summary>
    /// <remarks>
    /// UseShellExecute dispatches through the registered handler for whatever
    /// scheme is given - file://, a UNC path, or a custom protocol, not just
    /// http(s). Every caller in this app is expected to pass a genuine https
    /// URL; refusing anything else here is the last line of defense in case a
    /// value from an external source (an update feed, a pasted string) ever
    /// reaches this call unsanitized.
    /// </remarks>
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warn("Refused to open a non-https URL.");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not open a browser: {e.Message}");
            return false;
        }
    }

    /// <summary>Opens Explorer with the log file selected.</summary>
    public static void ShowLogFolder()
    {
        if (Log.FilePath is not { } path)
        {
            MessageBox.Show("No log file has been created yet.", "ClaudeCounter",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // /select needs the file to exist; fall back to the folder if the
            // app has not written a line yet.
            using var process = File.Exists(path)
                ? Process.Start("explorer.exe", $"/select,\"{path}\"")
                : Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn($"Could not open the log folder: {e.Message}");
        }
    }

    private const string IconResource = "ClaudeCounter.ico";

    /// <summary>
    /// The app icon, at its natural multi-resolution size. Callers must not
    /// dispose it - Form.Icon does not take ownership and the same instance is
    /// handed to every dialog.
    /// </summary>
    public static Icon? AppIcon() => CachedIcon.Value;

    /// <summary>The app icon rendered at a specific size, or null if unavailable.</summary>
    public static Icon? AppIcon(int size)
    {
        if (CachedIcon.Value is not { } icon)
            return null;
        try
        {
            return new Icon(icon, size, size);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static readonly Lazy<Icon?> CachedIcon = new(() =>
    {
        try
        {
            using var stream = typeof(Shell).Assembly.GetManifestResourceStream(IconResource);
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            Log.Warn($"Could not load the embedded app icon: {e.Message}");
            return null;
        }
    });
}

using System.ComponentModel;
using System.Diagnostics;
using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

/// <summary>
/// Whether a system tray actually exists to show the icon in. Stock GNOME
/// ships no tray at all - every StatusNotifierItem app needs the user to
/// install the "AppIndicator and KStatusNotifierItem Support" extension
/// first - while KDE, XFCE, Cinnamon and MATE provide one natively. Checking
/// for a StatusNotifierWatcher on the session bus tells them apart without
/// guessing at which desktop environment is running.
/// </summary>
public static class TrayPresence
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// True when a tray host is present, or when this could not be
    /// determined (dbus-send missing, no session bus, ...) - never nag the
    /// user over a guess.
    /// </summary>
    public static bool Available()
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[]
            {
                "--session", "--print-reply", "--dest=org.freedesktop.DBus",
                "/org/freedesktop/DBus", "org.freedesktop.DBus.NameHasOwner",
                "string:org.kde.StatusNotifierWatcher",
            })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return true;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // drain, ignore

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                return true;
            }

            return process.ExitCode != 0 || stdoutTask.GetAwaiter().GetResult().Contains("boolean true");
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            return true;
        }
    }

    public static void WarnIfMissing()
    {
        if (Available())
            return;

        Log.Warn("No system tray host detected (no org.kde.StatusNotifierWatcher on the session bus). " +
                 "On GNOME, install the 'AppIndicator and KStatusNotifierItem Support' extension.");
        Shell.Notify("ClaudeCounter",
            "No system tray was found. On GNOME, install the \"AppIndicator and KStatusNotifierItem " +
            "Support\" extension so the tray icon can appear.");
    }
}

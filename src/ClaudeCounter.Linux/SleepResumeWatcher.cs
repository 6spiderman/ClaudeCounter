using System.ComponentModel;
using System.Diagnostics;
using ClaudeCounter.Core;

namespace ClaudeCounter;

/// <summary>
/// Fires when the system resumes from sleep, via systemd-logind's
/// PrepareForSleep D-Bus signal on the system bus (boolean false = resuming,
/// true = about to sleep - only the former matters here). The Windows build
/// gets this for free from SystemEvents.PowerModeChanged; there is no BCL
/// equivalent on Linux, and shelling out to dbus-monitor and parsing its
/// output matches this project's existing preference (see Shell.cs,
/// TrayPresence.cs) for a well-known CLI over a bespoke D-Bus client.
/// </summary>
/// <remarks>
/// Best-effort like everything else platform-specific here: on a non-systemd
/// system, or one with no dbus-monitor, or without permission to monitor the
/// system bus, this simply never fires and the app just waits for its next
/// poll - exactly what happened before this existed.
/// </remarks>
public sealed class SleepResumeWatcher : IDisposable
{
    private readonly Process? _process;
    private readonly CancellationTokenSource _cts = new();

    public event Action? Resumed;

    public SleepResumeWatcher()
    {
        _process = TryStart();
        if (_process is not null)
            _ = WatchAsync(_process, _cts.Token);
    }

    private static Process? TryStart()
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-monitor")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--system");
            psi.ArgumentList.Add(
                "type='signal',interface='org.freedesktop.login1.Manager',member='PrepareForSleep'");
            return Process.Start(psi);
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            Log.Warn($"Could not start dbus-monitor for sleep/resume detection: {e.Message}");
            return null;
        }
    }

    private async Task WatchAsync(Process process, CancellationToken ct)
    {
        // Drained, not parsed: dbus-monitor prints its "falling back to
        // eavesdropping" permission notice here, which is expected and not
        // an error, but the pipe must still be read or the child can block.
        _ = Task.Run(() => process.StandardError.ReadToEndAsync(ct), ct);

        var expectingBoolean = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line is null)
                    break; // dbus-monitor exited

                if (line.Contains("member=PrepareForSleep"))
                {
                    expectingBoolean = true;
                }
                else if (expectingBoolean)
                {
                    expectingBoolean = false;
                    if (line.Contains("boolean false"))
                        Resumed?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposing.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        try
        {
            if (_process is { HasExited: false })
                _process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        _process?.Dispose();
    }
}

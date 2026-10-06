using ClaudeCounter.Core;
using Tmds.DBus.Protocol;

namespace ClaudeCounter;

/// <summary>
/// Fires when the system resumes from sleep, via systemd-logind's
/// PrepareForSleep D-Bus signal on the system bus (boolean false = resuming,
/// true = about to sleep - only the former matters here). The Windows build
/// gets this for free from SystemEvents.PowerModeChanged; there is no BCL
/// equivalent on Linux.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes in-process with Tmds.DBus.Protocol - the D-Bus client Avalonia
/// already ships for its tray icon, so this adds no new dependency to the
/// published binary. An earlier version shelled out to dbus-monitor instead,
/// but a child process outlives the app whenever the app is stopped abruptly
/// (the X server going away at logout ends the process from inside Xlib, with
/// no chance to clean up), leaving an orphaned dbus-monitor behind.
/// </para>
/// <para>
/// Best-effort like everything else platform-specific here: on a non-systemd
/// system, or with no system bus, this simply never fires and the app just
/// waits for its next poll.
/// </para>
/// </remarks>
public sealed class SleepResumeWatcher : IDisposable
{
    private readonly object _gate = new();
    private DBusConnection? _connection;
    private IDisposable? _subscription;
    private bool _disposed;

    public event Action? Resumed;

    public SleepResumeWatcher()
    {
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        DBusConnection? connection = null;
        try
        {
            if (DBusAddress.System is not { } address)
            {
                Log.Warn("No system D-Bus address; sleep/resume detection is disabled.");
                return;
            }

            connection = new DBusConnection(address);
            await connection.ConnectAsync();

            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Path = "/org/freedesktop/login1",
                Interface = "org.freedesktop.login1.Manager",
                Member = "PrepareForSleep",
            };
            var subscription = await connection.AddMatchAsync(
                rule,
                static (Message message, object? _) => message.GetBodyReader().ReadBool(),
                (Notification<bool> notification) =>
                {
                    // Completions (connection closed, observer disposed) carry
                    // no value and are not a signal.
                    if (notification.HasValue && !notification.Value)
                        Resumed?.Invoke();
                },
                false, // emitOnCapturedContext
                ObserverFlags.None,
                null); // readerState

            lock (_gate)
            {
                if (!_disposed)
                {
                    _connection = connection;
                    _subscription = subscription;
                    return;
                }
            }
            // Disposed while connecting.
            subscription.Dispose();
            connection.Dispose();
        }
        catch (Exception e)
        {
            Log.Warn($"Could not subscribe to sleep/resume notifications: {e.Message}");
            connection?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _subscription?.Dispose();
        _connection?.Dispose();
    }
}

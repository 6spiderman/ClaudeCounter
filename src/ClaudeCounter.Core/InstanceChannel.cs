using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ClaudeCounter.Core;

/// <summary>
/// A local, per-user command channel between ClaudeCounter processes: the
/// running tray app listens, and a second launch (or <c>claudecounter
/// --refresh</c>, or the Plasma widget) sends it a one-word command.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="System.IO.Pipes"/>: a named pipe on Windows, a Unix
/// domain socket on Linux. <see cref="PipeOptions.CurrentUserOnly"/> on both
/// ends means only processes of the same user can connect or be connected to.
/// The name includes the user, and on Windows the login session, matching the
/// single-instance mutex (which is per session there via <c>Local\</c>).
/// </para>
/// <para>
/// Best-effort throughout: if the channel cannot be created the app runs
/// exactly as before, and a sender that finds no listener just gets false.
/// Nothing here may delay exit or logout - the listener is a background task
/// cancelled by <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class InstanceChannel : IDisposable
{
    /// <summary>Bring up the flyout.</summary>
    public const string Show = "show";

    /// <summary>Poll now.</summary>
    public const string Refresh = "refresh";

    /// <summary>Do nothing; answers whether a listener is there at all.</summary>
    public const string Ping = "ping";

    private static readonly HashSet<string> KnownCommands = new(StringComparer.Ordinal) { Show, Refresh, Ping };

    private readonly string _pipeName;
    private readonly Action<string> _onCommand;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The pipe name for this user (and, on Windows, this login session).</summary>
    public static string DefaultPipeName { get; } = BuildDefaultName();

    private InstanceChannel(string pipeName, Action<string> onCommand)
    {
        _pipeName = pipeName;
        _onCommand = onCommand;
    }

    /// <summary>
    /// Starts listening in the background. <paramref name="onCommand"/> is
    /// called on a thread-pool thread with <see cref="Show"/> or
    /// <see cref="Refresh"/> (never <see cref="Ping"/>); callers marshal to
    /// their UI thread themselves.
    /// </summary>
    public static InstanceChannel StartServer(Action<string> onCommand, string? pipeName = null)
    {
        var channel = new InstanceChannel(pipeName ?? DefaultPipeName, onCommand);
        _ = channel.ListenAsync(channel._cts.Token);
        return channel;
    }

    /// <summary>
    /// Sends <paramref name="command"/> to the running instance. True when a
    /// listener accepted it; false when nothing is listening (the app is not
    /// running) or it did not answer within <paramref name="timeout"/>.
    /// Never throws.
    /// </summary>
    public static bool TrySend(string command, TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            return SendAsync(command, pipeName ?? DefaultPipeName, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when a ClaudeCounter tray app is running for this user.</summary>
    public static bool IsRunning(string? pipeName = null) =>
        TrySend(Ping, TimeSpan.FromMilliseconds(500), pipeName);

    private static async Task<bool> SendAsync(string command, string pipeName, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(ct).ConfigureAwait(false);

        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
        var reply = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        return reply == "ok";
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(2)); // a client that never writes cannot stall the loop
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                var command = (await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false))?.Trim() ?? "";
                var known = KnownCommands.Contains(command);
                await writer.WriteLineAsync((known ? "ok" : "unknown").AsMemory(), readTimeout.Token).ConfigureAwait(false);

                if (!known)
                    Log.Warn("Instance channel: ignored an unknown command.");
                else if (command != Ping)
                    Dispatch(command);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // A client that disconnected early or timed out - keep listening.
            }
            catch (Exception e)
            {
                // Could not create the pipe at all (e.g. a permissions problem):
                // the app works fine without it, so stop trying rather than spin.
                Log.Warn($"Instance channel unavailable: {e.Message}");
                return;
            }
        }
    }

    // A failing handler must not end the listener (the generic catch below
    // would treat it as "pipe unavailable" and stop for good).
    private void Dispatch(string command)
    {
        try
        {
            _onCommand(command);
        }
        catch (Exception e)
        {
            Log.Warn($"Instance channel: handling '{command}' failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private static string BuildDefaultName()
    {
        var user = new string(Environment.UserName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        if (!OperatingSystem.IsWindows())
            return $"ClaudeCounter.{user}";
        using var self = Process.GetCurrentProcess();
        return $"ClaudeCounter.{user}.{self.SessionId}";
    }
}

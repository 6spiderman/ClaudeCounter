using Avalonia;
using Avalonia.Controls;
using ClaudeCounter.Core;

namespace ClaudeCounter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Named Mutex works the same way on Linux as it does on Windows
        // (verified directly: a second process sees createdNew=false while
        // the first is running, and true again once it exits) - no "Local\"
        // prefix here, since that is a Windows Terminal Services convention
        // with no meaning on Linux.
        using var mutex = new Mutex(initiallyOwned: true, "ClaudeCounter_SingleInstance", out var createdNew);
        if (!createdNew)
            return 0;

        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (OperationCanceledException e)
        {
            // Not conditioned on TrayApplicationContext.IsExiting: confirmed
            // on a real shutdown that the session's D-Bus bus can be torn
            // down before our own SIGTERM handler gets to run, which cancels
            // Avalonia's tray-icon watch and surfaces here first - IsExiting
            // would still be false at that point even though this is an
            // entirely ordinary shutdown, not a bug. Any cancellation
            // reaching all the way up through Avalonia's dispatcher loop to
            // here is by construction something asking us to stop, never a
            // genuine unhandled error.
            Log.Info($"Exiting on a cancellation from the platform ({e.GetType().Name}) - normal during shutdown/logout.");
            return 0;
        }
        catch (Exception e)
        {
            Log.Error($"Unhandled exception on startup: {e}");
            Console.Error.WriteLine(
                $"ClaudeCounter hit an unexpected error and has to close.\n{e}\n\n" +
                $"Details were written to: {Log.FilePath}\n" +
                $"Please report this at {AppInfo.RepoUrl}/issues");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

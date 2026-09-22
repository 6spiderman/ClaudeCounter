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
        catch (OperationCanceledException) when (TrayApplicationContext.IsExiting)
        {
            // A benign race in Avalonia's own tray-icon teardown can still
            // surface a cancellation through the dispatcher on the way out
            // even without the toggle above; ExitApplication already logged
            // the real reason we are here.
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

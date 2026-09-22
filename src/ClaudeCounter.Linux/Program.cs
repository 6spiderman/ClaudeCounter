using Avalonia;
using Avalonia.Controls;
using ClaudeCounter.Core;

namespace ClaudeCounter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
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

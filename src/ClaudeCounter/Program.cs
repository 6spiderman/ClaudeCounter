using ClaudeCounter.Core;

namespace ClaudeCounter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance: the mutex must stay referenced for process lifetime.
        using var mutex = new Mutex(initiallyOwned: true,
            @"Local\ClaudeCounter_SingleInstance", out var createdNew);
        if (!createdNew)
            return;

        // Without these, an unhandled exception dies in the Windows Error
        // Reporting dialog and leaves nothing in our own log - so the user has
        // nothing to report and we have nothing to diagnose.
        Application.ThreadException += (_, e) => Fatal(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal(e.ExceptionObject as Exception, "background thread");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception e)
        {
            // Startup failures happen before the message loop exists, so
            // ThreadException never sees them.
            Fatal(e, "startup");
        }
    }

    private static void Fatal(Exception? exception, string where)
    {
        var detail = exception?.ToString() ?? "unknown error";
        Log.Error($"Unhandled exception on the {where}: {detail}");

        var logHint = Log.FilePath is { } path
            ? $"\n\nDetails were written to:\n{path}"
            : "";

        MessageBox.Show(
            $"ClaudeCounter hit an unexpected error and has to close.\n\n" +
            $"{exception?.GetType().Name}: {exception?.Message}{logHint}\n\n" +
            $"Please report this at {AppInfo.RepoUrl}/issues",
            "ClaudeCounter",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

        Environment.Exit(1);
    }
}

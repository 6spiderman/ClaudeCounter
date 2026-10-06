using ClaudeCounter.Core;

namespace ClaudeCounter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // `ClaudeCounter.exe --status` and the other options print and exit
        // straight away - before the single-instance mutex, so they work while
        // the tray app is running. See CommandLine for what each one does.
        if (CommandLine.IsCommand(args))
        {
            NativeMethods.AttachToParentConsoleIfNeeded();
            return CommandLine.Run(args, Console.Out, Console.Error, CommandLine.Environment.Real);
        }

        // Single instance: the mutex must stay referenced for process lifetime.
        using var mutex = new Mutex(initiallyOwned: true,
            @"Local\ClaudeCounter_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // Already running: launching it again (Start menu, desktop
            // shortcut) brings up the running instance's flyout instead of
            // doing nothing. If it does not answer, exit quietly as before.
            InstanceChannel.TrySend(InstanceChannel.Show, TimeSpan.FromSeconds(1));
            return 0;
        }

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
        return 0;
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

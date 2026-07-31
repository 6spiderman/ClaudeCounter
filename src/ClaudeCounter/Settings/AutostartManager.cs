using Microsoft.Win32;

namespace ClaudeCounter.Settings;

public static class AutostartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeCounter";

    // Environment.ProcessPath is correct under single-file publish,
    // unlike Assembly.Location which is empty there.
    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine executable path");

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{ExePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Self-heal the registered path if the exe was moved.</summary>
    public static void EnsurePathCurrent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is string current && current != $"\"{ExePath}\"")
            key.SetValue(ValueName, $"\"{ExePath}\"");
    }
}

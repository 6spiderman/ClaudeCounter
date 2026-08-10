using System.Diagnostics;

namespace ClaudeBackup;

/// <summary>
/// Result of running an external process. <see cref="Ok"/> is a convenience
/// for the common "exit code 0 means success" case; callers with a different
/// success convention (e.g. git commit, where "nothing to commit" also exits
/// non-zero) inspect <see cref="ExitCode"/>/<see cref="StdOut"/> directly.
/// </summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Seam between backend code (GitBackend, RcloneBackend, the restore engine,
/// ...) and actually shelling out, so those backends are unit-testable with a
/// fake runner and never need to invoke a real external binary in tests.
///
/// Lives in ClaudeCounter.Shared (not ClaudeBackup.csproj, where it was
/// originally defined) for the same reason BackupConfig and SecretDenylist
/// do - see their doc comments. The restore engine (src/ClaudeCounter.Shared/Restore)
/// needs it and is itself in Shared so the tray's future RestoreDialog can call
/// it in-process without ClaudeCounter.csproj taking a ProjectReference on
/// ClaudeBackup.csproj (that reference is what breaks the tray's single-file
/// publish - see BackupTaskManager's doc comment). The namespace stays
/// "ClaudeBackup" so every existing caller in ClaudeBackup.csproj
/// (GitBackend, RcloneBackend, Program.cs) keeps compiling unchanged; only the
/// physical file moved.
/// </summary>
public interface IProcessRunner
{
    ProcessResult Run(string file, IReadOnlyList<string> args, string? workingDir = null);
    bool Exists(string file);
}

/// <summary>Real implementation: starts an actual child process.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public ProcessResult Run(string file, IReadOnlyList<string> args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? "",
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }

    public bool Exists(string file)
    {
        // `where` walks PATH on Windows; also succeeds for absolute paths that exist.
        try
        {
            var r = Run("where", new[] { file });
            return r.Ok;
        }
        catch
        {
            return File.Exists(file);
        }
    }
}

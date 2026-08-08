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
/// Seam between backend code (GitBackend, RcloneBackend, ...) and actually
/// shelling out, so those backends are unit-testable with a fake runner and
/// never need to invoke a real external binary in tests.
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

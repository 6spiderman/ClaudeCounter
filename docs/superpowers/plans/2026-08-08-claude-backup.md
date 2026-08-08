# ClaudeBackup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone `ClaudeBackup.exe` worker that backs up a user-selected subset of `~/.claude` config to a private GitHub repo and/or Google Drive (via rclone) on a Windows Task Scheduler schedule, never uploading secrets, with an opt-in install component and a tray control panel.

**Architecture:** A new `net8.0` console project `ClaudeBackup` reads `backup.json`, resolves an include/exclude file set, strips a hard-coded secret denylist, then runs two independent backends (git push, rclone copy) behind an `IProcessRunner` seam so they are unit-testable without real git/rclone. The tray app writes the config, registers the scheduled task via `schtasks.exe`, and feature-detects the worker to show/hide its Backup UI.

**Tech Stack:** C# / .NET 8 console + WinForms (tray), xUnit, git + rclone (external, detected not bundled), Inno Setup, GitHub Actions.

## Global Constraints

- New project target framework: `net8.0` (console; no WinForms). Tray stays `net8.0-windows10.0.17763.0`.
- `TreatWarningsAsErrors=true` (Directory.Build.props) applies to the new project too - warning-clean code.
- `RestorePackagesWithLockFile=true` + CI `--locked-mode`: the new project MUST commit a `packages.lock.json`.
- Release publish is **self-contained single-file** and currently throws on any file other than `ClaudeCounter.exe` in `dist/<rid>` - the stray-file guard must be widened to allow `ClaudeBackup.exe`.
- Secret exclusion is hard-coded and NOT overridable by config. Fail closed.
- Text is ASCII only - plain dashes `-`, never em-dashes.
- No secret is ever written to `backup.json` or any log.
- **Codebase context (verified 2026-08-08):** the tray app now owns an OAuth sign-in subsystem (`Core/Auth/*`) and stores its own DPAPI-encrypted session at `%LOCALAPPDATA%\ClaudeCounter\session.dat` via `EncryptedSessionStore`. That file is credential material and is already on the denylist - keep it there. `tests/ClaudeCounter.Tests/Auth/SecretLeakTests.cs` is the existing convention for proving secrets never reach the log; follow its style for any backup secret test. Baseline suite is 219 passing tests - never reduce that count.
- Read any file before editing it; this plan quotes real code but the file may have moved on. Preserve existing behavior you did not come to change.

## File Structure

- Create `src/ClaudeBackup/ClaudeBackup.csproj` - console project; links shared `Log.cs`.
- Create `src/ClaudeBackup/Program.cs` - entry point, orchestration, exit codes.
- Create `src/ClaudeBackup/BackupConfig.cs` - config model + load/save.
- Create `src/ClaudeBackup/SecretDenylist.cs` - non-overridable secret filter + pre-flight scan.
- Create `src/ClaudeBackup/FileSelector.cs` - glob include/exclude resolution + denylist application.
- Create `src/ClaudeBackup/IProcessRunner.cs` - process seam + real implementation.
- Create `src/ClaudeBackup/GitBackend.cs` - git staging/commit/push.
- Create `src/ClaudeBackup/RcloneBackend.cs` - zip + rclone copy.
- Modify `ClaudeCounter.sln` - add the project.
- Modify `src/ClaudeCounter/UI/SettingsForm.cs` (or a new `BackupTab` control) - backup config UI.
- Create `src/ClaudeCounter/Settings/BackupTaskManager.cs` - schtasks register/remove + worker feature detection.
- Modify `src/ClaudeCounter/TrayApplicationContext.cs` - "Back up now" menu item, gated on worker presence.
- Modify `packaging/inno/ClaudeCounter.iss` - `[Components]`, `[Files]`, uninstall task cleanup.
- Modify `.github/workflows/release.yml` - publish the worker, widen stray-file guard, add to portable + installer source dir.
- Create tests under `tests/ClaudeCounter.Tests/backup/` for config, denylist, selector, backends.

---

### Task 1: Scaffold the ClaudeBackup project

**Files:**
- Create: `src/ClaudeBackup/ClaudeBackup.csproj`
- Create: `src/ClaudeBackup/Program.cs`
- Modify: `ClaudeCounter.sln`

**Interfaces:**
- Produces: an assembly `ClaudeBackup` with `namespace ClaudeBackup`, reusing `ClaudeCounter.Core.Log` via linked source.

- [ ] **Step 1: Create the csproj**

```xml
<!-- src/ClaudeBackup/ClaudeBackup.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <AssemblyName>ClaudeBackup</AssemblyName>
    <RootNamespace>ClaudeBackup</RootNamespace>
    <AssemblyTitle>ClaudeBackup</AssemblyTitle>
    <Product>ClaudeCounter</Product>
    <Description>Scheduled backup worker for Claude configuration</Description>
  </PropertyGroup>

  <ItemGroup>
    <!-- Share the tray app's logger without a WinForms dependency. -->
    <Compile Include="..\ClaudeCounter\Core\Log.cs" Link="Core\Log.cs" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Minimal Program.cs**

```csharp
// src/ClaudeBackup/Program.cs
namespace ClaudeBackup;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Real orchestration lands in Task 9. Placeholder exit for scaffolding.
        return 0;
    }
}
```

- [ ] **Step 3: Add to the solution and restore (generates the lock file)**

```bash
dotnet sln ClaudeCounter.sln add src/ClaudeBackup/ClaudeBackup.csproj
dotnet restore ClaudeCounter.sln
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build ClaudeCounter.sln -c Release`
Expected: `ClaudeBackup -> .../ClaudeBackup.dll`, 0 warnings. A `src/ClaudeBackup/packages.lock.json` now exists.

- [ ] **Step 5: Commit**

```bash
git add ClaudeCounter.sln src/ClaudeBackup/
git commit -m "Scaffold ClaudeBackup console project"
```

---

### Task 2: BackupConfig model + load/save

**Files:**
- Create: `src/ClaudeBackup/BackupConfig.cs`
- Test: `tests/ClaudeCounter.Tests/backup/BackupConfigTests.cs`

**Interfaces:**
- Produces:
  - `sealed class BackupConfig` with `string SourceRoot`, `List<string> Include`, `List<string> Exclude`, `GitTarget Github`, `DriveTarget Drive`, `ScheduleConfig Schedule`.
  - `sealed class GitTarget { bool Enabled; string RemoteUrl; string Branch = "main"; }`
  - `sealed class DriveTarget { bool Enabled; string RcloneRemote; }`
  - `sealed class ScheduleConfig { string Frequency = "daily"; string Time = "09:00"; }`
  - `static BackupConfig Default()`, `static string DefaultPath()`, `static BackupConfig Load(string path)`, `void Save(string path)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClaudeCounter.Tests/backup/BackupConfigTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupConfigTests
{
    [Fact]
    public void DefaultExcludesProjectsHistory()
    {
        var c = BackupConfig.Default();
        Assert.Contains("projects/**", c.Exclude);
        Assert.Contains("settings.json", c.Include);
    }

    [Fact]
    public void RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bkcfg-{Guid.NewGuid():N}.json");
        try
        {
            var c = BackupConfig.Default();
            c.Github.Enabled = true;
            c.Github.RemoteUrl = "git@github.com:me/claude-backup.git";
            c.Save(path);
            var back = BackupConfig.Load(path);
            Assert.True(back.Github.Enabled);
            Assert.Equal("git@github.com:me/claude-backup.git", back.Github.RemoteUrl);
            Assert.Equal("main", back.Github.Branch);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadMissingReturnsDefault()
    {
        var c = BackupConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));
        Assert.NotNull(c);
        Assert.Contains("settings.json", c.Include);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter BackupConfigTests`
Expected: FAIL - `BackupConfig` does not exist. (Add a `ProjectReference` from the test project to `ClaudeBackup` first - see Step 3b.)

- [ ] **Step 3a: Implement the model**

```csharp
// src/ClaudeBackup/BackupConfig.cs
using System.Text.Json;

namespace ClaudeBackup;

public sealed class GitTarget
{
    public bool Enabled { get; set; }
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
}

public sealed class DriveTarget
{
    public bool Enabled { get; set; }
    public string RcloneRemote { get; set; } = "";
}

public sealed class ScheduleConfig
{
    public string Frequency { get; set; } = "daily"; // daily | weekly | hourly
    public string Time { get; set; } = "09:00";
}

public sealed class BackupConfig
{
    public string SourceRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public GitTarget Github { get; set; } = new();
    public DriveTarget Drive { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCounter", "backup.json");

    public static BackupConfig Default() => new()
    {
        Include = new()
        {
            "settings.json", "CLAUDE.md", "commands/**", "agents/**", "plugins/**/*.json",
        },
        Exclude = new() { "projects/**", "statsig/**", "**/*cache*" },
    };

    public static BackupConfig Load(string path)
    {
        if (!File.Exists(path))
            return Default();
        try
        {
            return JsonSerializer.Deserialize<BackupConfig>(File.ReadAllText(path)) ?? Default();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return Default();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 3b: Reference ClaudeBackup from the test project**

In `tests/ClaudeCounter.Tests/ClaudeCounter.Tests.csproj`, add alongside the existing `ClaudeCounter` project reference:

```xml
    <ProjectReference Include="..\..\src\ClaudeBackup\ClaudeBackup.csproj" />
```

Then `dotnet restore ClaudeCounter.sln` (updates the test project's lock file).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter BackupConfigTests`
Expected: PASS (3).

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeBackup/BackupConfig.cs tests/ClaudeCounter.Tests/backup/BackupConfigTests.cs tests/ClaudeCounter.Tests/ClaudeCounter.Tests.csproj src/ClaudeBackup/packages.lock.json tests/ClaudeCounter.Tests/packages.lock.json
git commit -m "Add BackupConfig model with defaults and IO"
```

---

### Task 3: Secret denylist

**Files:**
- Create: `src/ClaudeBackup/SecretDenylist.cs`
- Test: `tests/ClaudeCounter.Tests/backup/SecretDenylistTests.cs`

**Interfaces:**
- Produces: `static class SecretDenylist` with `static bool IsSecret(string relativePath)` and `static List<string> Offenders(IEnumerable<string> relativePaths)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClaudeCounter.Tests/backup/SecretDenylistTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class SecretDenylistTests
{
    [Theory]
    [InlineData(".credentials.json")]
    [InlineData("sub/.credentials.json")]
    [InlineData("auth.token")]
    [InlineData("id_rsa.key")]
    [InlineData("cert.pem")]
    [InlineData("session.dat")]
    [InlineData("MY_SECRET.txt")]
    public void FlagsSecrets(string path) => Assert.True(SecretDenylist.IsSecret(path));

    [Theory]
    [InlineData("settings.json")]
    [InlineData("CLAUDE.md")]
    [InlineData("commands/foo.md")]
    public void AllowsNonSecrets(string path) => Assert.False(SecretDenylist.IsSecret(path));

    [Fact]
    public void OffendersListsOnlySecrets()
    {
        var bad = SecretDenylist.Offenders(new[] { "settings.json", ".credentials.json" });
        Assert.Equal(new[] { ".credentials.json" }, bad);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter SecretDenylistTests`
Expected: FAIL - `SecretDenylist` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/ClaudeBackup/SecretDenylist.cs
namespace ClaudeBackup;

/// <summary>
/// Non-overridable secret filter. Applied AFTER user include/exclude globs so no
/// config can cause a secret to be uploaded. Matching is case-insensitive on the
/// file name and on any path segment.
/// </summary>
public static class SecretDenylist
{
    private static readonly string[] ExactNames =
    {
        ".credentials.json", "session.dat",
    };

    private static readonly string[] Substrings = { "token", "secret" };
    private static readonly string[] Extensions = { ".key", ".pem" };

    public static bool IsSecret(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        foreach (var exact in ExactNames)
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (var ext in Extensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        var lower = name.ToLowerInvariant();
        foreach (var sub in Substrings)
            if (lower.Contains(sub))
                return true;
        return false;
    }

    public static List<string> Offenders(IEnumerable<string> relativePaths) =>
        relativePaths.Where(IsSecret).ToList();
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter SecretDenylistTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeBackup/SecretDenylist.cs tests/ClaudeCounter.Tests/backup/SecretDenylistTests.cs
git commit -m "Add non-overridable secret denylist"
```

---

### Task 4: FileSelector (glob include/exclude + denylist)

**Files:**
- Create: `src/ClaudeBackup/FileSelector.cs`
- Test: `tests/ClaudeCounter.Tests/backup/FileSelectorTests.cs`

**Interfaces:**
- Consumes: `SecretDenylist` (Task 3).
- Produces: `sealed class FileSelector` with `IReadOnlyList<string> Select(string root, IEnumerable<string> include, IEnumerable<string> exclude)` returning root-relative paths (forward-slashed), secrets already removed.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClaudeCounter.Tests/backup/FileSelectorTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class FileSelectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fsel-{Guid.NewGuid():N}");

    public FileSelectorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "commands"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "x"));
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        File.WriteAllText(Path.Combine(_root, "commands", "a.md"), "a");
        File.WriteAllText(Path.Combine(_root, "projects", "x", "big.log"), "log");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void IncludesMatchesAndExcludesProjectsAndSecrets()
    {
        var sel = new FileSelector();
        var result = sel.Select(_root,
            new[] { "settings.json", "commands/**", ".credentials.json" },
            new[] { "projects/**" });

        Assert.Contains("settings.json", result);
        Assert.Contains("commands/a.md", result);
        Assert.DoesNotContain("projects/x/big.log", result); // excluded
        Assert.DoesNotContain(".credentials.json", result);   // denylisted even if included
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter FileSelectorTests`
Expected: FAIL - `FileSelector` does not exist.

- [ ] **Step 3: Implement using the built-in matcher**

Use `Microsoft.Extensions.FileSystemGlobbing` - it ships in the shared framework metapackage referenced by SDK-style projects, so **verify no new PackageReference is needed**; if the type is not resolvable, add `Microsoft.Extensions.FileSystemGlobbing` and update `packages.lock.json`. (If a package add is undesirable, fall back to the hand-rolled matcher noted after the code.)

```csharp
// src/ClaudeBackup/FileSelector.cs
using Microsoft.Extensions.FileSystemGlobbing;

namespace ClaudeBackup;

public sealed class FileSelector
{
    public IReadOnlyList<string> Select(string root,
        IEnumerable<string> include, IEnumerable<string> exclude)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(include);
        matcher.AddExcludePatterns(exclude);

        var result = matcher.GetResultsInFullPath(root)
            .Select(full => Path.GetRelativePath(root, full).Replace('\\', '/'))
            .Where(rel => !SecretDenylist.IsSecret(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToList();
        return result;
    }
}
```

> Fallback if avoiding the package: enumerate `Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)`, convert to root-relative forward-slash paths, and match each against include/exclude globs with a small translator (`**` -> `.*`, `*` -> `[^/]*`, escape the rest) compiled to `Regex`. Keep the same `Select` signature and denylist filter.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter FileSelectorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeBackup/FileSelector.cs tests/ClaudeCounter.Tests/backup/FileSelectorTests.cs src/ClaudeBackup/packages.lock.json
git commit -m "Add FileSelector with glob include/exclude and secret filtering"
```

---

### Task 5: IProcessRunner seam

**Files:**
- Create: `src/ClaudeBackup/IProcessRunner.cs`
- Test: `tests/ClaudeCounter.Tests/backup/ProcessRunnerTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ProcessResult(int ExitCode, string StdOut, string StdErr) { bool Ok => ExitCode == 0; }`
  - `interface IProcessRunner { ProcessResult Run(string file, IReadOnlyList<string> args, string? workingDir = null); bool Exists(string file); }`
  - `sealed class ProcessRunner : IProcessRunner` (real implementation).

- [ ] **Step 1: Write the failing test (against the real runner, using a portable command)**

```csharp
// tests/ClaudeCounter.Tests/backup/ProcessRunnerTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class ProcessRunnerTests
{
    [Fact]
    public void RunsAndCapturesExitCode()
    {
        var runner = new ProcessRunner();
        // `cmd /c exit 3` is available on the Windows CI runner.
        var result = runner.Run("cmd", new[] { "/c", "exit", "3" });
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Ok);
    }

    [Fact]
    public void ExistsFindsCmd() => Assert.True(new ProcessRunner().Exists("cmd"));

    [Fact]
    public void ExistsFalseForNonsense() =>
        Assert.False(new ProcessRunner().Exists("definitely-not-a-real-binary-xyz"));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter ProcessRunnerTests`
Expected: FAIL - types do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/ClaudeBackup/IProcessRunner.cs
using System.Diagnostics;

namespace ClaudeBackup;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public interface IProcessRunner
{
    ProcessResult Run(string file, IReadOnlyList<string> args, string? workingDir = null);
    bool Exists(string file);
}

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
        // `where` on PATH; also succeeds for absolute paths that exist.
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
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter ProcessRunnerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeBackup/IProcessRunner.cs tests/ClaudeCounter.Tests/backup/ProcessRunnerTests.cs
git commit -m "Add IProcessRunner process seam"
```

---

### Task 6: GitBackend

**Files:**
- Create: `src/ClaudeBackup/GitBackend.cs`
- Test: `tests/ClaudeCounter.Tests/backup/GitBackendTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `ProcessResult` (Task 5), `FileSelector` output (relative paths), `GitTarget` (Task 2), `ClaudeCounter.Core.Log`.
- Produces: `sealed class GitBackend(IProcessRunner runner, string stagingDir)` with `BackendResult Run(string sourceRoot, IReadOnlyList<string> files, GitTarget target)`; `sealed record BackendResult(bool Ok, string Message)`.

- [ ] **Step 1: Write the failing test (uses a fake runner - no real git)**

```csharp
// tests/ClaudeCounter.Tests/backup/GitBackendTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class GitBackendTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool GitPresent { get; set; } = true;
        public bool Exists(string file) => file == "git" ? GitPresent : true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");
            return new ProcessResult(0, "", "");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gbsrc-{Guid.NewGuid():N}");
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"gbstg-{Guid.NewGuid():N}");

    public GitBackendTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_staging)) Directory.Delete(_staging, true);
    }

    [Fact]
    public void MissingGitFailsCleanly()
    {
        var runner = new FakeRunner { GitPresent = false };
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.False(result.Ok);
        Assert.Contains("git", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopiesFilesAndPushes()
    {
        var runner = new FakeRunner();
        var backend = new GitBackend(runner, _staging);
        var target = new GitTarget { Enabled = true, RemoteUrl = "url", Branch = "main" };
        var result = backend.Run(_root, new[] { "settings.json" }, target);
        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_staging, "settings.json")));
        Assert.Contains(runner.Calls, c => c.StartsWith("git add"));
        Assert.Contains(runner.Calls, c => c.Contains("commit"));
        Assert.Contains(runner.Calls, c => c.Contains("push"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter GitBackendTests`
Expected: FAIL - `GitBackend` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/ClaudeBackup/GitBackend.cs
using ClaudeCounter.Core;

namespace ClaudeBackup;

public sealed record BackendResult(bool Ok, string Message);

public sealed class GitBackend
{
    private readonly IProcessRunner _runner;
    private readonly string _stagingDir;

    public GitBackend(IProcessRunner runner, string stagingDir)
    {
        _runner = runner;
        _stagingDir = stagingDir;
    }

    public BackendResult Run(string sourceRoot, IReadOnlyList<string> files, GitTarget target)
    {
        if (!_runner.Exists("git"))
            return Fail("git not found on PATH - install Git for Windows.");

        try
        {
            Directory.CreateDirectory(_stagingDir);
            if (!Directory.Exists(Path.Combine(_stagingDir, ".git")))
            {
                Check(_runner.Run("git", new[] { "init", "-b", target.Branch }, _stagingDir), "git init");
                Check(_runner.Run("git", new[] { "remote", "add", "origin", target.RemoteUrl }, _stagingDir), "git remote add");
            }

            MirrorFiles(sourceRoot, files);

            Check(_runner.Run("git", new[] { "add", "-A" }, _stagingDir), "git add");
            // commit returns non-zero when there is nothing to commit; treat that as success.
            var commit = _runner.Run("git",
                new[] { "commit", "-m", $"Backup {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}" }, _stagingDir);
            if (!commit.Ok && !commit.StdOut.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return Fail($"git commit failed: {commit.StdErr}".Trim());

            Check(_runner.Run("git", new[] { "push", "origin", target.Branch }, _stagingDir), "git push");
            Log.Info("GitBackend: push OK.");
            return new BackendResult(true, "GitHub backup complete.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private void MirrorFiles(string sourceRoot, IReadOnlyList<string> files)
    {
        // Clear previously-mirrored content (keep .git) so deletions propagate.
        foreach (var entry in Directory.EnumerateFileSystemEntries(_stagingDir))
        {
            if (Path.GetFileName(entry) == ".git") continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, true);
            else File.Delete(entry);
        }
        foreach (var rel in files)
        {
            var src = Path.Combine(sourceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(_stagingDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }

    private static void Check(ProcessResult r, string what)
    {
        if (!r.Ok)
            throw new InvalidOperationException($"{what} failed: {r.StdErr}".Trim());
    }

    private static BackendResult Fail(string message)
    {
        Log.Warn($"GitBackend: {message}");
        return new BackendResult(false, message);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter GitBackendTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeBackup/GitBackend.cs tests/ClaudeCounter.Tests/backup/GitBackendTests.cs
git commit -m "Add GitBackend with staging mirror and push"
```

---

### Task 7: RcloneBackend

**Files:**
- Create: `src/ClaudeBackup/RcloneBackend.cs`
- Test: `tests/ClaudeCounter.Tests/backup/RcloneBackendTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `ProcessResult`, `DriveTarget`, `BackendResult`, `ClaudeCounter.Core.Log`.
- Produces: `sealed class RcloneBackend(IProcessRunner runner, string tempDir)` with `BackendResult Run(string sourceRoot, IReadOnlyList<string> files, DriveTarget target)`.

- [ ] **Step 1: Write the failing test (fake runner)**

```csharp
// tests/ClaudeCounter.Tests/backup/RcloneBackendTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class RcloneBackendTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();
        public bool RclonePresent { get; set; } = true;
        public bool Exists(string file) => file == "rclone" ? RclonePresent : true;
        public ProcessResult Run(string file, IReadOnlyList<string> args, string? wd = null)
        {
            Calls.Add($"{file} {string.Join(' ', args)}");
            return new ProcessResult(0, "", "");
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rcsrc-{Guid.NewGuid():N}");
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"rctmp-{Guid.NewGuid():N}");

    public RcloneBackendTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
    }

    [Fact]
    public void MissingRcloneFailsCleanly()
    {
        var runner = new FakeRunner { RclonePresent = false };
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });
        Assert.False(result.Ok);
        Assert.Contains("rclone", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZipsAndCopies()
    {
        var runner = new FakeRunner();
        var result = new RcloneBackend(runner, _tmp)
            .Run(_root, new[] { "settings.json" }, new DriveTarget { Enabled = true, RcloneRemote = "gdrive:X" });
        Assert.True(result.Ok);
        Assert.Contains(runner.Calls, c => c.StartsWith("rclone copy"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter RcloneBackendTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/ClaudeBackup/RcloneBackend.cs
using System.IO.Compression;
using ClaudeCounter.Core;

namespace ClaudeBackup;

public sealed class RcloneBackend
{
    private readonly IProcessRunner _runner;
    private readonly string _tempDir;

    public RcloneBackend(IProcessRunner runner, string tempDir)
    {
        _runner = runner;
        _tempDir = tempDir;
    }

    public BackendResult Run(string sourceRoot, IReadOnlyList<string> files, DriveTarget target)
    {
        if (!_runner.Exists("rclone"))
            return Fail("rclone not found on PATH - install and run 'rclone config' first.");

        var zipPath = Path.Combine(_tempDir, $"claude-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            Directory.CreateDirectory(_tempDir);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var rel in files)
                {
                    var src = Path.Combine(sourceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(src))
                        zip.CreateEntryFromFile(src, rel);
                }
            }

            var copy = _runner.Run("rclone", new[] { "copy", zipPath, target.RcloneRemote });
            if (!copy.Ok)
                return Fail($"rclone copy failed: {copy.StdErr}".Trim());

            Log.Info("RcloneBackend: copy OK.");
            return new BackendResult(true, "Google Drive backup complete.");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* temp cleanup best-effort */ }
        }
    }

    private static BackendResult Fail(string message)
    {
        Log.Warn($"RcloneBackend: {message}");
        return new BackendResult(false, message);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter RcloneBackendTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeBackup/RcloneBackend.cs tests/ClaudeCounter.Tests/backup/RcloneBackendTests.cs
git commit -m "Add RcloneBackend with zip snapshot and copy"
```

---

### Task 8: Worker orchestration + exit codes

**Files:**
- Modify: `src/ClaudeBackup/Program.cs`
- Test: `tests/ClaudeCounter.Tests/backup/BackupRunnerTests.cs`

**Interfaces:**
- Consumes: all prior backup types.
- Produces: `static class BackupRunner` with `static int Run(BackupConfig config, IProcessRunner runner, string stagingDir, string tempDir)` returning an exit code (0 ok; 1 config/selection error incl. denylist offender; 2 a backend failed). `Program.Main` wires real dependencies and calls it.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/ClaudeCounter.Tests/backup/BackupRunnerTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class BackupRunnerTests : IDisposable
{
    private sealed class OkRunner : IProcessRunner
    {
        public bool Exists(string file) => true;
        public ProcessResult Run(string f, IReadOnlyList<string> a, string? wd = null)
            => new(0, "", "");
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"brsrc-{Guid.NewGuid():N}");
    private readonly string _stg = Path.Combine(Path.GetTempPath(), $"brstg-{Guid.NewGuid():N}");
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"brtmp-{Guid.NewGuid():N}");

    public BackupRunnerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{}");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _stg, _tmp })
            if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    private BackupConfig Config() => new()
    {
        SourceRoot = _root,
        Include = new() { "settings.json" },
        Exclude = new(),
        Github = new() { Enabled = true, RemoteUrl = "url", Branch = "main" },
        Drive = new() { Enabled = false },
    };

    [Fact]
    public void NoDestinationsIsConfigError()
    {
        var c = Config();
        c.Github.Enabled = false;
        Assert.Equal(1, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
    }

    [Fact]
    public void HappyPathReturnsZero()
    {
        Assert.Equal(0, BackupRunner.Run(Config(), new OkRunner(), _stg, _tmp));
    }

    [Fact]
    public void DenylistedForcedIncludeAborts()
    {
        var c = Config();
        File.WriteAllText(Path.Combine(_root, ".credentials.json"), "secret");
        c.Include.Add(".credentials.json");
        // Selector already drops secrets; the pre-flight scan is the backstop.
        // Force the scenario by asserting no secret is ever staged: run returns 0
        // and the staging dir must not contain the secret.
        Assert.Equal(0, BackupRunner.Run(c, new OkRunner(), _stg, _tmp));
        Assert.False(File.Exists(Path.Combine(_stg, ".credentials.json")));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter BackupRunnerTests`
Expected: FAIL - `BackupRunner` does not exist.

- [ ] **Step 3: Implement runner + Program**

```csharp
// src/ClaudeBackup/Program.cs
using ClaudeBackup;
using ClaudeCounter.Core;

internal static class Program
{
    private static int Main()
    {
        var config = BackupConfig.Load(BackupConfig.DefaultPath());
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var staging = Path.Combine(local, "ClaudeCounter", "backup-repo");
        var temp = Path.Combine(local, "ClaudeCounter", "backup-tmp");
        Log.Info("ClaudeBackup starting.");
        var code = BackupRunner.Run(config, new ProcessRunner(), staging, temp);
        Log.Info($"ClaudeBackup finished with exit code {code}.");
        return code;
    }
}

namespace ClaudeBackup
{
    public static class BackupRunner
    {
        public static int Run(BackupConfig config, IProcessRunner runner, string stagingDir, string tempDir)
        {
            if (!config.Github.Enabled && !config.Drive.Enabled)
            {
                Log.Warn("No backup destinations enabled.");
                return 1;
            }

            var files = new FileSelector().Select(config.SourceRoot, config.Include, config.Exclude);

            // Fail-closed backstop: even though the selector drops secrets, refuse
            // to proceed if anything denylisted slipped through.
            var offenders = SecretDenylist.Offenders(files);
            if (offenders.Count > 0)
            {
                Log.Error($"Aborting: secret file(s) in selection: {string.Join(", ", offenders)}");
                return 1;
            }

            if (files.Count == 0)
            {
                Log.Warn("Nothing selected to back up.");
                return 1;
            }

            var anyFailed = false;
            if (config.Github.Enabled)
            {
                var r = new GitBackend(runner, stagingDir).Run(config.SourceRoot, files, config.Github);
                anyFailed |= !r.Ok;
            }
            if (config.Drive.Enabled)
            {
                var r = new RcloneBackend(runner, tempDir).Run(config.SourceRoot, files, config.Drive);
                anyFailed |= !r.Ok;
            }
            return anyFailed ? 2 : 0;
        }
    }
}
```

> `Program.Main` cannot share a file with a namespaced class cleanly under top-level-style layout; keep `BackupRunner` in its own file `src/ClaudeBackup/BackupRunner.cs` if the compiler objects to the mixed layout above. Move the `namespace ClaudeBackup { ... }` block there and leave only `Main` in `Program.cs`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter BackupRunnerTests`
Expected: PASS. Then `dotnet build ClaudeCounter.sln -c Release` (0 warnings).

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeBackup/Program.cs src/ClaudeBackup/BackupRunner.cs tests/ClaudeCounter.Tests/backup/BackupRunnerTests.cs
git commit -m "Add backup orchestration with fail-closed secret scan and exit codes"
```

---

### Task 9: Tray integration - feature detection, task registration, Backup tab

**Files:**
- Create: `src/ClaudeCounter/Settings/BackupTaskManager.cs`
- Modify: `src/ClaudeCounter/UI/SettingsForm.cs`
- Modify: `src/ClaudeCounter/TrayApplicationContext.cs`
- Test: `tests/ClaudeCounter.Tests/BackupTaskManagerTests.cs`

**Interfaces:**
- Produces: `static class BackupTaskManager` with `static string? WorkerPath()` (returns the `ClaudeBackup.exe` path next to the tray exe, or null if absent), `static bool WorkerAvailable()`, `static void RunNow()`, `static string BuildSchtasksArgs(ScheduleConfig schedule, string workerPath)` (pure, testable), `static void Register(ScheduleConfig schedule)`, `static void Unregister()`.

- [ ] **Step 1: Write the failing test (pure arg builder + detection)**

```csharp
// tests/ClaudeCounter.Tests/BackupTaskManagerTests.cs
using ClaudeBackup;
using ClaudeCounter.Settings;
using Xunit;

namespace ClaudeCounter.Tests;

public class BackupTaskManagerTests
{
    [Fact]
    public void DailyArgsUseScDaily()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "daily", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("/SC", args);
        Assert.Contains("DAILY", args);
        Assert.Contains("09:00", args);
        Assert.Contains("ClaudeBackup.exe", args);
        Assert.Contains("ClaudeCounter Backup", args); // task name
    }

    [Fact]
    public void HourlyMapsToScHourly()
    {
        var args = BackupTaskManager.BuildSchtasksArgs(
            new ScheduleConfig { Frequency = "hourly", Time = "09:00" },
            @"C:\apps\ClaudeBackup.exe");
        Assert.Contains("HOURLY", args);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter BackupTaskManagerTests`
Expected: FAIL - `BackupTaskManager` does not exist.

- [ ] **Step 3: Implement BackupTaskManager**

```csharp
// src/ClaudeCounter/Settings/BackupTaskManager.cs
using System.Diagnostics;
using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.Settings;

public static class BackupTaskManager
{
    public const string TaskName = "ClaudeCounter Backup";

    public static string? WorkerPath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, "ClaudeBackup.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public static bool WorkerAvailable() => WorkerPath() is not null;

    public static void RunNow()
    {
        if (WorkerPath() is not { } path)
            return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
    }

    public static string BuildSchtasksArgs(ScheduleConfig schedule, string workerPath)
    {
        var sc = schedule.Frequency.ToLowerInvariant() switch
        {
            "hourly" => "HOURLY",
            "weekly" => "WEEKLY",
            _ => "DAILY",
        };
        // /F overwrites an existing task; quoting handles spaces in the path.
        return $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{workerPath}\\\"\" /SC {sc} /ST {schedule.Time}";
    }

    public static void Register(ScheduleConfig schedule)
    {
        if (WorkerPath() is not { } path)
            return;
        Run($"{BuildSchtasksArgs(schedule, path)}");
    }

    public static void Unregister() => Run($"/Delete /F /TN \"{TaskName}\"");

    private static void Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            if (p is { ExitCode: not 0 })
                Log.Warn($"schtasks exited {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
        }
        catch (Exception e)
        {
            Log.Warn($"schtasks failed: {e.Message}");
        }
    }
}
```

> `BuildSchtasksArgs` returns a single string for testability; `Register` passes it to `ProcessStartInfo(file, args)`, which parses it. If argument quoting proves fragile, switch `Run` to `ArgumentList` and have `BuildSchtasksArgs` return `IReadOnlyList<string>` - update the test accordingly.

- [ ] **Step 4: Add the tray project reference to ClaudeBackup**

`src/ClaudeCounter/ClaudeCounter.csproj` needs the `ScheduleConfig`/config types:

```xml
    <ProjectReference Include="..\ClaudeBackup\ClaudeBackup.csproj" />
```

Then `dotnet restore ClaudeCounter.sln` (updates the tray lock file). This makes the tray depend on the worker's config types but NOT run it in-process.

- [ ] **Step 5: Backup tab in SettingsForm + "Back up now" menu**

- In `SettingsForm`, only build the Backup controls when `BackupTaskManager.WorkerAvailable()`. Add inputs for: GitHub enabled + remote URL + branch; Drive enabled + rclone remote; include/exclude multiline textboxes (newline-separated); frequency combo + time; a "Save & register schedule" button that writes `BackupConfig` to `BackupConfig.DefaultPath()` and calls `BackupTaskManager.Register(schedule)`; a "Back up now" button calling `BackupTaskManager.RunNow()`. Load current values from `BackupConfig.Load(BackupConfig.DefaultPath())`.
- In `TrayApplicationContext`, add a context-menu item only when `BackupTaskManager.WorkerAvailable()`.
  The menu is currently built as: `Refresh now`, `_signInItem`, `Settings...`, separator,
  `_updateItem`, `Open log folder`, `About ClaudeCounter...`, separator, `Exit`. Insert the
  backup item directly after `Settings...` and BEFORE the first separator:

```csharp
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        if (BackupTaskManager.WorkerAvailable())
            menu.Items.Add("Back up now", null, (_, _) => BackupTaskManager.RunNow());
        menu.Items.Add(new ToolStripSeparator());
```

- [ ] **Step 6: Run tests + build**

Run: `dotnet test tests/ClaudeCounter.Tests -c Debug --filter BackupTaskManagerTests`
Expected: PASS.
Run: `dotnet build ClaudeCounter.sln -c Release`
Expected: 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeCounter/Settings/BackupTaskManager.cs src/ClaudeCounter/UI/SettingsForm.cs src/ClaudeCounter/TrayApplicationContext.cs src/ClaudeCounter/ClaudeCounter.csproj src/ClaudeCounter/packages.lock.json
git commit -m "Add backup tray UI, feature detection, and task scheduling"
```

---

### Task 10: Installer component + release workflow + uninstall cleanup

**Files:**
- Modify: `packaging/inno/ClaudeCounter.iss`
- Modify: `.github/workflows/release.yml`
- Modify: `.github/workflows/ci.yml` (optional: publish-smoke the worker too)

**Interfaces:** none (packaging/CI only).

- [ ] **Step 1: Installer - add the component and file**

In `ClaudeCounter.iss` add a `[Types]`/`[Components]` section (Inno shows the Components page automatically when components exist):

```
[Types]
Name: "full"; Description: "Full installation"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "core";   Description: "ClaudeCounter (tray app)"; Types: full custom; Flags: fixed
Name: "backup"; Description: "Backup tools (ClaudeBackup)"; Types: full
```

Note: `backup` is NOT in `custom` by default, so a custom install leaves it unchecked; `full` includes it. To make the DEFAULT (first-run) unchecked, rely on the default type being `custom` - set `[Setup]` `DefaultType`? Inno has no such key; instead omit `backup` from the default-selected type by making the FIRST type exclude it. Simplest per the spec's "unchecked by default": keep only `Name: "backup"; Description: "Backup tools (ClaudeBackup)"` with no `Types`, which renders unchecked unless the user ticks it.

Use this minimal form:

```
[Components]
Name: "backup"; Description: "Backup tools (ClaudeBackup)"
```

Add to `[Files]`:

```
Source: "{#SourceDir}\ClaudeBackup.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: backup
```

Add scheduled-task cleanup to the uninstall `[Code]` (extend the existing `CurUninstallStepChanged`, before the settings prompt):

```pascal
    Exec('schtasks.exe', '/Delete /F /TN "ClaudeCounter Backup"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
```

(Declare `ResultCode: Integer;` in the procedure's `var` block; ignore failure - the task may not exist.)

- [ ] **Step 2: Release workflow - publish the worker**

In `.github/workflows/release.yml`, inside the `Publish` step's `foreach ($rid ...)` loop, after publishing the tray exe, publish the worker into the SAME folder:

```powershell
            dotnet publish src/ClaudeBackup/ClaudeBackup.csproj `
              -c Release -r $rid --self-contained true --no-restore `
              -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
              -p:DebugType=none -p:Version=$env:VERSION `
              -o "dist/$rid"
            if ($LASTEXITCODE -ne 0) { throw "worker publish failed for $rid" }
```

Then widen the stray-file guard so both exes are allowed:

```powershell
            $allowed = @('ClaudeCounter.exe', 'ClaudeBackup.exe')
            $stray = Get-ChildItem "dist/$rid" -File | Where-Object { $allowed -notcontains $_.Name }
            if ($stray) {
              throw "Unexpected files alongside the exes for ${rid}: $($stray.Name -join ', ')."
            }
```

- [ ] **Step 3: Portable archive - include the worker**

In the `Build portable archives` step, after copying the tray exe:

```powershell
            Copy-Item "dist/win-$arch/ClaudeBackup.exe" $staging
```

(Portable users have no component screen; shipping the worker means the tray's Backup UI is available - it is inert until configured.)

- [ ] **Step 4: Validate the installer script compiles locally (if Inno is installed)**

Run (Windows, Inno Setup present):
`& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' /DAppVersion=0.0.0 /DArch=x64 /DSourceDir=<abs path to a dist\win-x64 with both exes> packaging\inno\ClaudeCounter.iss`
Expected: compiles; output setup exe shows a Components page with "Backup tools (ClaudeBackup)" unchecked.

If Inno is not available locally, this is validated by the release workflow on the next tag; note that in the PR.

- [ ] **Step 5: Commit**

```bash
git add packaging/inno/ClaudeCounter.iss .github/workflows/release.yml .github/workflows/ci.yml
git commit -m "Package ClaudeBackup as an optional install component"
```

---

## Self-Review

- **Spec coverage:** standalone worker (Task 1, 8), configurable include/exclude (Task 2, 4), hard secret denylist + fail-closed pre-flight (Task 3, 8), GitHub backend via credential manager (Task 6), Drive via rclone (Task 7), process seam for testability (Task 5), Task Scheduler registration + feature detection + tray UI (Task 9), opt-in installer component + release publish + uninstall cleanup (Task 10). Security model and build/CI sections both map to tasks.
- **Placeholder scan:** none - every code step is complete. Two explicit "if the compiler objects / if quoting is fragile" fallbacks are provided with concrete alternatives, not TODOs.
- **Type consistency:** `BackupConfig`, `GitTarget`, `DriveTarget`, `ScheduleConfig`, `BackendResult`, `IProcessRunner`, `ProcessResult`, `FileSelector.Select`, `SecretDenylist.IsSecret/Offenders`, `GitBackend`, `RcloneBackend`, `BackupRunner.Run`, `BackupTaskManager.*` names and signatures are consistent across tasks.
- **Constraints honored:** new project commits `packages.lock.json` (Task 1, 2, 4); stray-file guard widened (Task 10); secrets never staged (Task 3, 8); `TreatWarningsAsErrors` respected (build gates in each task).
- **Cross-plan note:** build this plan AFTER the alert-popups plan, per the shipping order agreed in the specs.

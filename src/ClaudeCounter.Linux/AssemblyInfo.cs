using System.Runtime.Versioning;

// This build only ever runs on Linux, so Linux-only APIs (File.SetUnixFileMode,
// ...) are always safe here - unlike ClaudeCounter.Core, which is deliberately
// platform-neutral and must not gain a platform-specific call without a guard.
[assembly: SupportedOSPlatform("linux")]

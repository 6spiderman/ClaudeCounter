namespace ClaudeBackup;

/// <summary>
/// Shared "is this relative path safe to write under a root directory"
/// check for backend output. Both <see cref="GitBackend"/> (staging a file
/// under its staging directory) and <see cref="RcloneBackend"/> (naming a
/// zip entry) take an arbitrary <c>IReadOnlyList&lt;string&gt;</c> of files
/// as public API - <see cref="FileSelector"/> is expected to hand back
/// root-relative, forward-slashed paths with no ".." segment, but neither
/// backend may assume that. One check, one place to fix it, instead of two
/// copies drifting apart (the same reasoning that moved credential
/// scrubbing into <see cref="CredentialScrubber"/>).
/// </summary>
internal static class RelativePathGuard
{
    /// <summary>
    /// Rejects absolute paths, backslashes, empty segments, and "." / ".."
    /// segments so a malformed or malicious relative path can never resolve
    /// outside the directory it is about to be written under.
    /// </summary>
    internal static bool IsSafe(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (Path.IsPathRooted(rel)) return false;
        if (rel.Contains('\\')) return false;

        foreach (var segment in rel.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }
        return true;
    }
}

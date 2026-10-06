namespace ClaudeBackup;

/// <summary>
/// Shared "is this relative path safe to write under a root directory"
/// check for backend output. <see cref="GitBackend"/> (staging a file under
/// its staging directory), <see cref="RcloneBackend"/> (naming a zip entry),
/// and the restore engine (extracting a Drive zip entry, or copying a
/// checked-out GitHub commit's tree, into a staging/live directory) all take
/// an arbitrary relative path from a source that must not be trusted blindly
/// - a FileSelector selection, a zip archive that could have been tampered
/// with, or a git tree. One check, one place to fix it, instead of several
/// copies drifting apart (the same reasoning that moved credential scrubbing
/// into <see cref="CredentialScrubber"/>).
///
/// Lives in ClaudeCounter.Core (not ClaudeBackup.csproj, where it was
/// originally defined) so the restore engine - itself in Core, for the
/// reasons documented on <see cref="IProcessRunner"/> - can reuse it rather
/// than duplicating the containment logic. GitBackend and RcloneBackend keep
/// using it unchanged; only the physical file moved, and the namespace did
/// not change.
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

    /// <summary>
    /// True when <paramref name="candidateFullPath"/> is <paramref name="baseDirFull"/>
    /// itself or somewhere underneath it. Separator-aware on purpose: a naive
    /// <c>StartsWith(baseDirFull)</c> would wrongly accept a sibling directory
    /// whose name happens to share the same prefix (e.g. "C:\staging-evil"
    /// against a base of "C:\staging"). Moved here from GitBackend (which
    /// originally had its own private copy of exactly this check) so both it
    /// and the restore engine share one implementation - see
    /// GitBackend.IsWithinDirectory, now a thin forwarding wrapper kept only
    /// so GitBackendTests' existing direct calls keep compiling unchanged.
    /// </summary>
    internal static bool IsWithinDirectory(string baseDirFull, string candidateFullPath)
    {
        var normalizedBase = Path.TrimEndingDirectorySeparator(baseDirFull);
        return string.Equals(candidateFullPath, normalizedBase, StringComparison.OrdinalIgnoreCase)
            || candidateFullPath.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are the same
    /// directory, or one is nested inside the other (in either direction).
    /// Fix round 1 (restore review, Important 3): a materialisation
    /// destination that IS, or CONTAINS, or is CONTAINED BY, a protected
    /// live root must be refused before anything is written - restore rule 1
    /// ("never write to live config without an explicit apply call") held
    /// only by convention until this check existed, since neither
    /// RestoreZipSource.Materialize nor RestoreGitSource.Materialize
    /// otherwise has any idea what "live" means. Both directions matter: a
    /// destination equal to or under the live root would write config
    /// directly into it, and a destination that is an ANCESTOR of the live
    /// root would (combined with the destination-clearing fix in the same
    /// review round) delete everything under that ancestor, live root
    /// included, before materialising into it.
    /// </summary>
    internal static bool Overlaps(string a, string b)
    {
        var af = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var bf = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return IsWithinDirectory(af, bf) || IsWithinDirectory(bf, af);
    }
}

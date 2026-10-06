using System.Security.Cryptography;
using ClaudeCounter.Core;

namespace ClaudeBackup;

/// <summary>
/// Pure preview-classification logic (design spec step 4): compares an
/// already-materialised, already-safe staging folder (see <see
/// cref="RestoreZipSource"/> / <see cref="RestoreGitSource"/> - both enforce
/// containment and the secret denylist on the way IN, before this class ever
/// sees the folder) against the live ~/.claude tree, and classifies every
/// file as New / Changed / Identical / LiveOnly.
///
/// No process invocation, no WinForms dependency, nothing beyond plain
/// filesystem enumeration and (only when two files' lengths already match) a
/// hash of each - this is what makes it safe to run off the UI thread, which
/// is the entire reason it is a separate static class rather than folded into
/// the future restore dialog.
///
/// Walks both trees by hand, mirroring FileSelector/ClaudeLocationScanner's
/// own approach: one unreadable file or directory is skipped rather than
/// aborting the whole comparison, and a reparse point (symlink/junction) is
/// never followed, since it could point outside the tree being walked.
/// </summary>
public static class RestoreClassifier
{
    /// <summary>
    /// Classifies every file under <paramref name="stagedRoot"/> and/or
    /// <paramref name="liveRoot"/>. Either root may be absent (a live
    /// ~/.claude that does not exist yet on a fresh machine, or a
    /// materialisation with nothing selected) - an absent root is treated as
    /// contributing zero files rather than throwing.
    /// </summary>
    public static IReadOnlyList<RestoreFileEntry> Classify(string stagedRoot, string liveRoot)
    {
        var staged = EnumerateRelativeFiles(stagedRoot);
        var live = EnumerateRelativeFiles(liveRoot);

        var allPaths = new SortedSet<string>(staged.Keys, StringComparer.Ordinal);
        allPaths.UnionWith(live.Keys);

        var entries = new List<RestoreFileEntry>(allPaths.Count);
        foreach (var rel in allPaths)
        {
            var hasStaged = staged.TryGetValue(rel, out var stagedInfo);
            var hasLive = live.TryGetValue(rel, out var liveInfo);

            if (hasStaged && !hasLive)
            {
                entries.Add(new RestoreFileEntry(
                    rel, RestoreFileStatus.New,
                    stagedInfo!.Length, stagedInfo.ModifiedUtc,
                    null, null));
                continue;
            }

            if (!hasStaged && hasLive)
            {
                entries.Add(new RestoreFileEntry(
                    rel, RestoreFileStatus.LiveOnly,
                    null, null,
                    liveInfo!.Length, liveInfo.ModifiedUtc));
                continue;
            }

            // Present on both sides: length first, and a hash only when
            // lengths already match - the optimisation the design spec calls
            // for, so a large tree with mostly-unrelated files never pays for
            // reading bytes it can already tell differ by length alone. Two
            // files of equal length with different content (the case a naive
            // length-only compare gets wrong) still reach IsSameContent and
            // are correctly reported as Changed.
            var identical = stagedInfo!.Length == liveInfo!.Length
                && IsSameContent(Path.Combine(stagedRoot, ToFsPath(rel)), Path.Combine(liveRoot, ToFsPath(rel)));

            entries.Add(new RestoreFileEntry(
                rel,
                identical ? RestoreFileStatus.Identical : RestoreFileStatus.Changed,
                stagedInfo.Length, stagedInfo.ModifiedUtc,
                liveInfo.Length, liveInfo.ModifiedUtc));
        }

        return entries;
    }

    private static string ToFsPath(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Hashes both files and compares digests. Only ever called once lengths
    /// are already known to match (see <see cref="Classify"/>) - this is not
    /// itself a length check. A file that cannot be read at this point
    /// (locked, permission denied, deleted between enumeration and here) is
    /// conservatively treated as NOT identical rather than throwing out of
    /// Classify: reporting a possibly-stale "Changed" is safe (worst case,
    /// the user is offered to re-apply a file that turns out to already
    /// match), while silently claiming "Identical" on a read failure could
    /// hide a real difference.
    /// </summary>
    private static bool IsSameContent(string stagedPath, string livePath)
    {
        try
        {
            return ComputeHash(stagedPath).AsSpan().SequenceEqual(ComputeHash(livePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"RestoreClassifier: could not hash '{stagedPath}' / '{livePath}' for comparison; treating as changed.");
            return false;
        }
    }

    private static byte[] ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private sealed record FileSnapshot(long Length, DateTimeOffset ModifiedUtc);

    private static Dictionary<string, FileSnapshot> EnumerateRelativeFiles(string root)
    {
        var result = new Dictionary<string, FileSnapshot>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
            return result;

        var realRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var stack = new Stack<string>();
        stack.Push(realRoot);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                Log.Warn($"RestoreClassifier: skipping unreadable directory '{dir}'.");
                continue;
            }

            foreach (var sub in subDirs)
            {
                if (IsReparsePoint(sub))
                    continue;

                // A materialised staging folder should never contain a .git
                // directory (RestoreGitSource copies the checked-out tree
                // OUT of its git worktree before returning), but this is
                // defense in depth against that invariant ever slipping, not
                // the primary mechanism.
                if (string.Equals(Path.GetFileName(sub), ".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                stack.Push(sub);
            }

            foreach (var file in files)
            {
                if (IsReparsePoint(file))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    var rel = Path.GetRelativePath(realRoot, file).Replace('\\', '/');
                    result[rel] = new FileSnapshot(info.Length, info.LastWriteTimeUtc);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"RestoreClassifier: skipping unreadable file '{file}'.");
                }
            }
        }

        return result;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fail closed, matching FileSelector/ClaudeLocationScanner: if we
            // cannot tell what it is, do not walk into it and do not count it.
            return true;
        }
    }
}

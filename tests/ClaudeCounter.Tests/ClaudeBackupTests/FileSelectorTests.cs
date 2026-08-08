// tests/ClaudeCounter.Tests/ClaudeBackupTests/FileSelectorTests.cs
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using ClaudeBackup;
using ClaudeCounter.Core;
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

    public void Dispose()
    {
        // Best-effort cleanup: a transiently locked file must not fail the
        // test, but a real failure to clean up should still be visible
        // rather than a silently leaked %TEMP% directory.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Directory.Delete(_root, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    Log.Warn($"FileSelectorTests: failed to clean up temp root '{_root}' after {attempt} attempts: {e.Message}");
                    return;
                }
                Thread.Sleep(50);
            }
        }
    }

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

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "SETTINGS.JSON" }, Array.Empty<string>());

        Assert.Contains("settings.json", result);
    }

    [Fact]
    public void NoIncludePatternsSelectsNothing()
    {
        var sel = new FileSelector();
        var result = sel.Select(_root, Array.Empty<string>(), Array.Empty<string>());

        Assert.Empty(result);
    }

    // Mirrors BackupConfig.Default().Include's "plugins/**/*.json": the glob
    // must reach both a plugin file directly under "plugins/" and one nested
    // under a per-plugin subdirectory, while the denylist still strips
    // anything secret-shaped that the glob picked up.
    [Fact]
    public void PluginJsonGlobIncludesNestedFilesButDenylistStripsSecretShapedOnes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "my-server"));
        File.WriteAllText(Path.Combine(_root, "plugins", "top.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "plugins", "my-server", "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "plugins", "my-server", "api_token.json"), "{\"token\":\"x\"}");

        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "plugins/**/*.json" }, Array.Empty<string>());

        Assert.Contains("plugins/top.json", result);
        Assert.Contains("plugins/my-server/manifest.json", result);
        Assert.DoesNotContain("plugins/my-server/api_token.json", result);
    }

    // Fix round 2 regression: round 1's directory pruning treated ANY
    // exclude pattern matching a directory's own path as license to skip
    // its entire contents. "**/*cache*" compiles to
    // "^(?:.*/)?[^/]*cache[^/]*$", which matches a directory literally named
    // "cache-helpers" - but that pattern only ever constrains a FILE's own
    // name, not "everything under a matching directory". Pruning on it
    // silently dropped commands/cache-helpers/foo.md even though the
    // pattern does not match that file's path at all. Only a pattern whose
    // text ends in "**" may prune a directory now.
    [Fact]
    public void DirectoryNameContainingCacheIsNotWhollyPrunedByNameOnlyPattern()
    {
        Directory.CreateDirectory(Path.Combine(_root, "commands", "cache-helpers"));
        File.WriteAllText(Path.Combine(_root, "commands", "cache-helpers", "foo.md"), "x");

        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "commands/**" }, new[] { "**/*cache*" });

        Assert.Contains("commands/cache-helpers/foo.md", result);
    }

    // Companion to the above: a pattern that DOES end in "**" (so it truly
    // implies every descendant also matches) must still prune the whole
    // subtree, and do so without even visiting files under it.
    [Fact]
    public void CacheDirectoryGlobstarPatternStillPrunesWholeSubtree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins", "cache"));
        File.WriteAllText(Path.Combine(_root, "plugins", "cache", "blob.json"), "x");
        File.WriteAllText(Path.Combine(_root, "plugins", "keep.json"), "{}");

        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "plugins/**" }, new[] { "**/cache/**" });

        Assert.Contains("plugins/keep.json", result);
        Assert.DoesNotContain("plugins/cache/blob.json", result);
    }

    // C2 (Fix round 1): "?" must behave as a glob single-character wildcard,
    // not leak through as an unescaped regex quantifier. Before the fix,
    // "commands/?.md" matched nothing (the literal "?" made the preceding
    // "." optional instead of meaning "one character"), and a leading "?"
    // made the start anchor itself optional so patterns like "?ecrets/x"
    // matched paths that did not start with anything close to that text.
    [Theory]
    [InlineData("a.md", "commands/?.md", true)]
    [InlineData("ab.md", "commands/?.md", false)]
    [InlineData("a.md", "commands/??.md", false)]
    public void QuestionMarkMatchesExactlyOneCharacter(string fileName, string pattern, bool expected)
    {
        File.WriteAllText(Path.Combine(_root, "commands", fileName), "x");

        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { pattern }, Array.Empty<string>());

        Assert.Equal(expected, result.Contains($"commands/{fileName}"));
    }

    [Fact]
    public void QuestionMarkDoesNotMakeTheStartAnchorOptional()
    {
        Directory.CreateDirectory(Path.Combine(_root, "secrets"));
        File.WriteAllText(Path.Combine(_root, "secrets", "notes.md"), "x");

        var sel = new FileSelector();
        // A single "?" must consume exactly one real character, so this
        // pattern requires a path that starts with some character followed
        // by literal "ecrets/notes.md" - "secrets/notes.md" only qualifies
        // if "?" matches the leading "s", which it does; the point of this
        // test is that it must NOT match via an accidentally-optional start.
        var result = sel.Select(_root, new[] { "?ecrets/notes.md" }, Array.Empty<string>());

        Assert.Contains("secrets/notes.md", result);

        // But it must not match a path that does not actually have a
        // single-character prefix in front of "ecrets/notes.md".
        Directory.CreateDirectory(Path.Combine(_root, "a", "b", "secrets"));
        File.WriteAllText(Path.Combine(_root, "a", "b", "secrets", "notes.md"), "x");
        var result2 = sel.Select(_root, new[] { "?ecrets/notes.md" }, Array.Empty<string>());
        Assert.DoesNotContain("a/b/secrets/notes.md", result2);
    }

    // I2 (Fix round 1): denylist drops must be visible to the caller, not
    // silent, since they are real data loss (a user's own include glob
    // legitimately matched a file like "token-usage.md").
    [Fact]
    public void WithheldOutParamReportsFilesDroppedBySecretDenylist()
    {
        File.WriteAllText(Path.Combine(_root, "token-usage.md"), "not actually a secret");

        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "*.md", "token-usage.md" }, Array.Empty<string>(), out var withheld);

        Assert.DoesNotContain("token-usage.md", result);
        Assert.Contains("token-usage.md", withheld);
    }

    [Fact]
    public void ThreeArgOverloadStillWorksWithoutObservingWithheldFiles()
    {
        File.WriteAllText(Path.Combine(_root, "token-usage.md"), "not actually a secret");

        var sel = new FileSelector();
        var result = sel.Select(_root, new[] { "token-usage.md" }, Array.Empty<string>());

        Assert.DoesNotContain("token-usage.md", result);
    }

    // I3 (Fix round 1): pattern text comes from user-editable backup.json, so
    // a pathological glob (repeated "a*" with no trailing match, the classic
    // catastrophic-backtracking shape) must not be able to hang the worker.
    // Each Regex carries a 2-second match timeout and fails closed: a timed
    // out INCLUDE counts as no-match, a timed-out EXCLUDE counts as a match.
    [Fact]
    public void PathologicalIncludePatternTimesOutInsteadOfHangingAndFailsClosed()
    {
        var target = new string('a', 30);
        File.WriteAllText(Path.Combine(_root, target), "x");
        var pathological = string.Concat(Enumerable.Repeat("a*", 20)) + "b";

        var sel = new FileSelector();
        var sw = Stopwatch.StartNew();
        var result = sel.Select(_root, new[] { pathological }, Array.Empty<string>());
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}, expected well under the 2s pattern timeout plus overhead");
        Assert.DoesNotContain(target, result);
    }

    [Fact]
    public void PathologicalExcludePatternTimesOutInsteadOfHangingAndFailsClosed()
    {
        var target = new string('a', 30);
        File.WriteAllText(Path.Combine(_root, target), "x");
        var pathological = string.Concat(Enumerable.Repeat("a*", 20)) + "b";

        var sel = new FileSelector();
        var sw = Stopwatch.StartNew();
        var result = sel.Select(_root, new[] { "*" }, new[] { pathological });
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}, expected well under the 2s pattern timeout plus overhead");
        Assert.DoesNotContain(target, result);
    }

    // C1 (Fix round 1): a junction/symlink under root must never be followed
    // - it can point anywhere on disk, turning an include glob into an
    // arbitrary-filesystem reader. Junctions (unlike symlinks) do not require
    // elevated privileges on NTFS, so this uses "mklink /J". If the sandbox
    // this runs in still refuses to create one, we say so explicitly and
    // return rather than silently reporting the path as covered.
    [Fact]
    public void DoesNotFollowJunctionsOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"fsel-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var linkPath = Path.Combine(_root, "commands", "linked");
        var linkCreated = false;
        try
        {
            File.WriteAllText(Path.Combine(outside, "id_rsa"), "outside-key-material");
            File.WriteAllText(Path.Combine(outside, "notes.txt"), "outside-notes");

            linkCreated = TryCreateJunction(linkPath, outside);
            if (!linkCreated)
            {
                if (OperatingSystem.IsWindows())
                {
                    // Junctions require no elevation on NTFS. If creation
                    // failed here anyway, this test's coverage of the C1 fix
                    // would otherwise evaporate silently - fail loudly
                    // instead of returning green with nothing exercised.
                    Assert.Fail("Could not create an NTFS junction on Windows; " +
                        "the junction-escape regression coverage did not run.");
                }

                // Non-Windows: junctions are not an NTFS concept here, so
                // this scenario does not apply. Still make the skip
                // observable rather than a quiet no-op.
                Log.Warn("FileSelectorTests: skipping junction-escape coverage - not running on Windows.");
                return;
            }

            var sel = new FileSelector();
            var result = sel.Select(_root, new[] { "**" }, Array.Empty<string>());

            Assert.DoesNotContain("commands/linked/id_rsa", result);
            Assert.DoesNotContain("commands/linked/notes.txt", result);
            Assert.All(result, p => Assert.DoesNotContain("linked", p));
        }
        finally
        {
            // Remove the junction itself (non-recursive - it is a link, not
            // real content) before the target it points at, and before the
            // outer Dispose() walks _root: deleting a directory tree that
            // still contains a junction pointing at an already-removed
            // target has been observed to throw UnauthorizedAccessException
            // on this runtime rather than just quietly unlinking it.
            if (linkCreated && Directory.Exists(linkPath))
            {
                try { Directory.Delete(linkPath, recursive: false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
            }
            Directory.Delete(outside, true);
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

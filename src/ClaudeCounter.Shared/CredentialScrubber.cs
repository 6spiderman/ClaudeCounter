using System.Text.RegularExpressions;

namespace ClaudeBackup;

/// <summary>
/// Shared credential-scrubbing choke point for backend output. <see
/// cref="GitBackend"/> (git stderr can echo an HTTPS remote with an embedded
/// token), <see cref="RcloneBackend"/> (rclone stderr can echo a remote spec,
/// e.g. a WebDAV/S3 URL with embedded credentials), and the restore engine's
/// GitHub source (a failed `git clone`/`git fetch` against an HTTPS remote can
/// equally echo a token) all route anything that could reach a log line, an
/// exception message, or a result message through here first. One regex, one
/// place to fix it, instead of copies drifting apart.
///
/// Lives in ClaudeCounter.Shared (not ClaudeBackup.csproj, where it was
/// originally defined) so the restore engine - itself in Shared, for the
/// reasons documented on <see cref="IProcessRunner"/> - can reuse it rather
/// than duplicating the regex. GitBackend and RcloneBackend keep using it
/// unchanged; only the physical file moved, and the namespace did not change.
/// </summary>
internal static class CredentialScrubber
{
    // Matches "<scheme>://<userinfo>@" so a user:token (or bare token)
    // embedded in a URL can be stripped wherever it appears in free text -
    // not just a clean remote URL, but also a full sentence of process
    // stderr like "fatal: unable to access 'https://<token>@github.com/...': ...".
    // Requiring an explicit "scheme://" prefix is what keeps this from
    // mangling the SSH shorthand form ("git@github.com:user/repo.git"),
    // where "git@" is a username, not a credential, and there is no "://" to
    // match against. The userinfo group deliberately swallows an optional
    // ":password" segment too (":" is not excluded from the character
    // class) rather than capturing user and password separately - both are
    // credential-shaped and neither should ever survive into a log.
    private static readonly Regex CredentialUrlRegex = new(
        @"\b(?<scheme>https?|ssh)://[^\s/@]+@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strips credential-bearing "user:pass@" / "token@" prefixes out of any
    /// URL found in <paramref name="text"/>. Safe to call on arbitrary free
    /// text (process stderr, exception messages) as well as a bare URL; text
    /// with no matching pattern - including the SSH shorthand form - passes
    /// through unchanged. Null/empty input returns "" rather than throwing.
    /// </summary>
    internal static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return CredentialUrlRegex.Replace(text, m => $"{m.Groups["scheme"].Value}://");
    }
}

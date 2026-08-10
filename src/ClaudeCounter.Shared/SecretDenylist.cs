namespace ClaudeBackup;

/// <summary>
/// Non-overridable secret filter. Applied AFTER user include/exclude globs so no
/// config can cause a secret to be uploaded. Matching is case-insensitive on the
/// file name (via <see cref="Path.GetFileName(string)"/>), not the full path.
///
/// S6: lives in ClaudeCounter.Shared (not ClaudeBackup.csproj) - same reasoning,
/// and same "keep the ClaudeBackup namespace even though the file physically
/// moved" pattern, as BackupConfig.cs. The Backup tab's file picker (in the
/// ClaudeCounter tray project) needs this to grey out denylisted nodes, and
/// ClaudeCounter deliberately carries no ProjectReference to ClaudeBackup.csproj
/// (see BackupTaskManager's doc comment: that would drag the worker's
/// RID-specific publish graph into the tray's single-file publish). Moving this
/// type here - rather than adding that reference, or duplicating the list - lets
/// both the worker (FileSelector) and the tray (the picker) share one
/// definition of what a secret looks like.
///
/// This is a NAME-based heuristic, not a content scanner. It reliably catches
/// the two concrete secrets known to exist on these machines
/// (".credentials.json" and "session.dat"), common credential-file names
/// (".env", SSH private keys, netrc/npmrc/git-credentials/pgpass files), and
/// common secret-shaped file names (things containing "token", "secret",
/// "credential", or an API-key spelling, and files with a ".key" / ".pem" /
/// other key-material extension - see <see cref="ExtensionSegments"/>). It
/// does NOT inspect file contents, so a secret embedded inside a file with an
/// innocuous name (for example a plugin or MCP server config called
/// "mcp.json" or "manifest.json" that has an API key baked into its JSON
/// body) will not be flagged here. See the FileSelector tests and the task
/// report for the honest list of what this does and does not catch.
/// </summary>
public static class SecretDenylist
{
    private static readonly string[] ExactNames =
    {
        ".credentials.json", "session.dat",
        ".env", ".netrc", "_netrc", ".npmrc", ".git-credentials", ".pgpass",
        "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa",
        // known_hosts is host-inventory recon (public host keys), not key
        // material - but nothing in a Claude config backup legitimately
        // needs it, and flagging it costs one string with no false-drop risk.
        "known_hosts",
    };

    private static readonly string[] Substrings =
    {
        "token", "secret", "credential", "apikey", "api_key", "api-key",
    };

    // Matched against dot-delimited components of the file name (not just the
    // final suffix), so a renamed/backed-up file like "cert.pem.bak" is still
    // caught - a plain EndsWith(".pem") would miss it because the file no
    // longer literally ends in ".pem".
    private static readonly string[] ExtensionSegments =
    {
        "key", "pem", "pfx", "p12", "jks", "keystore", "ppk", "asc", "gpg",
    };

    public static bool IsSecret(string relativePath)
    {
        var name = NormalizeName(Path.GetFileName(relativePath));

        foreach (var exact in ExactNames)
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase))
                return true;

        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
            foreach (var ext in ExtensionSegments)
                if (segment.Equals(ext, StringComparison.OrdinalIgnoreCase))
                    return true;

        var lower = name.ToLowerInvariant();
        foreach (var sub in Substrings)
            if (lower.Contains(sub))
                return true;

        return false;
    }

    public static List<string> Offenders(IEnumerable<string> relativePaths) =>
        relativePaths.Where(IsSecret).ToList();

    /// <summary>
    /// Strips cosmetic differences an attacker (or an innocent typo) could use
    /// to dodge a naive comparison: an NTFS alternate-data-stream suffix
    /// ("session.dat:hidden") and trailing spaces/dots that Windows itself
    /// ignores when resolving a path ("session.dat " / "session.dat.").
    /// </summary>
    private static string NormalizeName(string rawName)
    {
        var colonIndex = rawName.IndexOf(':');
        var withoutStream = colonIndex >= 0 ? rawName[..colonIndex] : rawName;
        return withoutStream.TrimEnd(' ', '.');
    }
}

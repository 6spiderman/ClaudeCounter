namespace ClaudeBackup;

/// <summary>
/// Non-overridable secret filter. Applied AFTER user include/exclude globs so no
/// config can cause a secret to be uploaded. Matching is case-insensitive on the
/// file name (via <see cref="Path.GetFileName(string)"/>), not the full path.
///
/// This is a NAME-based heuristic, not a content scanner. It reliably catches
/// the two concrete secrets known to exist on these machines
/// (".credentials.json" and "session.dat") plus common secret-shaped file
/// names (things containing "token", "secret", "credential", or an API-key
/// spelling, and files ending in ".key" / ".pem"). It does NOT inspect file
/// contents, so a secret embedded inside a file with an innocuous name (for
/// example a plugin or MCP server config called "mcp.json" or "manifest.json"
/// that has an API key baked into its JSON body) will not be flagged here.
/// See the FileSelector tests and the task report for the honest list of
/// what this does and does not catch.
/// </summary>
public static class SecretDenylist
{
    private static readonly string[] ExactNames =
    {
        ".credentials.json", "session.dat",
    };

    private static readonly string[] Substrings =
    {
        "token", "secret", "credential", "apikey", "api_key", "api-key",
    };

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

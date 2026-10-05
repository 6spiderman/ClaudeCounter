namespace ClaudeCounter.Core.Auth;

/// <summary>
/// Unpadded base64url per RFC 4648 section 5, which is what PKCE requires:
/// '+' becomes '-', '/' becomes '_', and the '=' padding is stripped.
/// </summary>
public static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

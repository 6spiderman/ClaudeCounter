using System.Security.Cryptography;
using System.Text;

namespace ClaudeCounter.Core.Auth;

/// <summary>
/// A PKCE verifier and its S256 challenge (RFC 7636). The verifier stays on
/// this machine; only the challenge goes to the authorization server, so an
/// attacker who intercepts the authorization code still cannot redeem it.
/// </summary>
public sealed record PkceCodes(string Verifier, string Challenge)
{
    /// <summary>32 bytes base64url-encodes to 43 characters, the RFC minimum.</summary>
    private const int VerifierBytes = 32;

    /// <param name="randomBytes">
    /// Injectable so tests can pin the verifier and assert the challenge
    /// against the RFC's reference vector. Production passes null.
    /// </param>
    public static PkceCodes Generate(Func<int, byte[]>? randomBytes = null)
    {
        var bytes = randomBytes?.Invoke(VerifierBytes) ?? RandomNumberGenerator.GetBytes(VerifierBytes);
        return FromVerifier(Base64Url.Encode(bytes));
    }

    public static PkceCodes FromVerifier(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return new PkceCodes(verifier, Base64Url.Encode(hash));
    }
}

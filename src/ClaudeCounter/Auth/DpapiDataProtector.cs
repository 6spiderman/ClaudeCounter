using System.Security.Cryptography;

namespace ClaudeCounter.Core.Auth;

/// <summary>
/// Windows DPAPI at CurrentUser scope. The key is derived from the user's
/// Windows credentials and is machine-bound: the file cannot be decrypted by
/// another account, or by the same account on a different PC. That last case is
/// normal - a portable copy carried to another machine - so it is reported as
/// "no session", not as an error.
/// </summary>
public sealed class DpapiDataProtector : IDataProtector
{
    // Mixed into the key. Not a secret; it just means a blob from this app
    // cannot be decrypted by a different app running as the same user.
    private static readonly byte[] Entropy = "ClaudeCounter.session.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[]? Unprotect(byte[] ciphertext)
    {
        try
        {
            return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Wrong user, wrong machine, or a corrupted file. All mean the same
            // thing to the caller: there is no usable session here.
            return null;
        }
    }
}

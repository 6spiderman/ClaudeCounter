namespace ClaudeCounter.Core.Auth;

/// <summary>
/// Seam over the platform's secret storage (DPAPI on Windows, Secret
/// Service/libsecret on Linux) so the session store can be tested without
/// touching either, and so each front end can plug in its own implementation.
/// </summary>
public interface IDataProtector
{
    byte[] Protect(byte[] plaintext);

    /// <summary>Returns null when the ciphertext cannot be decrypted.</summary>
    byte[]? Unprotect(byte[] ciphertext);
}

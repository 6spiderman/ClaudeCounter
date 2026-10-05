using System.Security.Cryptography;
using System.Text;
using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.Core.Auth;

/// <summary>
/// AES-GCM encryption using a key derived from <c>/etc/machine-id</c> and the
/// current username, so the session file is not sitting on disk in plain text.
/// </summary>
/// <remarks>
/// This is a stand-in for proper Secret Service (libsecret/GNOME
/// Keyring/KWallet) integration, which is the eventual target - see the
/// project's Linux-port plan. It matches DPAPI's actual threat model on
/// Windows reasonably well in the meantime: it stops a casual read of the raw
/// file or a copy carried to another machine or user, not another process
/// already running as you, which no local-storage scheme (including the OS
/// keyring, once unlocked) defends against either. It also works headlessly,
/// with no dependency on a running keyring daemon or D-Bus session.
/// </remarks>
public sealed class MachineKeyDataProtector : IDataProtector
{
    private const int NonceSize = 12; // AES-GCM standard nonce length
    private const int TagSize = 16;

    private readonly Lazy<byte[]> _key;

    public MachineKeyDataProtector(string? fallbackKeyPath = null)
    {
        _key = new Lazy<byte[]>(() => DeriveKey(fallbackKeyPath));
    }

    public byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);
        return result;
    }

    public byte[]? Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceSize + TagSize)
            return null;

        try
        {
            var nonce = ciphertext[..NonceSize];
            var tag = ciphertext[^TagSize..];
            var body = ciphertext[NonceSize..^TagSize];
            var plaintext = new byte[body.Length];

            using var aes = new AesGcm(_key.Value, TagSize);
            aes.Decrypt(nonce, body, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            // Wrong key (different machine or user), or a corrupted file. Both
            // mean the same thing to the caller: there is no usable session.
            return null;
        }
    }

    private static byte[] DeriveKey(string? fallbackKeyPath)
    {
        var material = ReadMachineId() ?? ReadOrCreateFallbackKey(fallbackKeyPath);
        return SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{material}:{Environment.UserName}:ClaudeCounter.session.v1"));
    }

    private static string? ReadMachineId()
    {
        try
        {
            return File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id").Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Only reached on a system with no /etc/machine-id (non-systemd, some
    // minimal containers). A random value, generated once and reused, plays
    // the same role machine-id would.
    private static string ReadOrCreateFallbackKey(string? path)
    {
        path ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCounter", "machine.key");

        try
        {
            if (File.Exists(path))
                return File.ReadAllText(path).Trim();

            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(path, key);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not persist a fallback machine key: {e.Message}");
            // Worst case: a key that changes every run, so the session simply
            // fails to decrypt next time and the user signs in again.
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }
    }
}

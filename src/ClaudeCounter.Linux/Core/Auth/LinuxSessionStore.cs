namespace ClaudeCounter.Core.Auth;

/// <summary>
/// Wraps <see cref="EncryptedSessionStore"/> to additionally restrict the
/// session file to the owner only (equivalent of the ACL a Windows user
/// profile already gives <c>%LocalAppData%</c>) after every write. Belt and
/// braces on top of <see cref="MachineKeyDataProtector"/>: even if the key
/// derivation were ever weakened, the file itself is not world-readable.
/// </summary>
public sealed class LinuxSessionStore : ISessionStore
{
    private readonly EncryptedSessionStore _inner;
    private readonly string _path;

    public LinuxSessionStore(IDataProtector protector, string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCounter", "session.dat");
        _inner = new EncryptedSessionStore(_path, protector);
    }

    public OAuthSession? Read() => _inner.Read();

    public void Write(OAuthSession session)
    {
        _inner.Write(session);
        TryLockDownPermissions();
    }

    public void Clear() => _inner.Clear();

    private void TryLockDownPermissions()
    {
        try
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Warn($"Could not restrict the session file's permissions: {e.Message}");
        }
    }
}

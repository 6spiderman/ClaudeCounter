using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCounter.Core.Auth;

/// <summary>ClaudeCounter's own OAuth session - separate from Claude Code's.</summary>
public sealed record OAuthSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? Scopes,
    DateTimeOffset ObtainedAt)
{
    /// <summary>Bumped if the shape changes; an unknown value reads as "no session".</summary>
    public int Version { get; init; } = CurrentVersion;

    public const int CurrentVersion = 1;

    public static OAuthSession FromTokens(OAuthTokens tokens, DateTimeOffset now) =>
        new(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt, tokens.Scopes, now);
}

public interface ISessionStore
{
    /// <summary>The stored session, or null if there is none. Never throws.</summary>
    OAuthSession? Read();

    void Write(OAuthSession session);

    void Clear();
}

/// <summary>
/// Persists the session to %LocalAppData%\ClaudeCounter\session.dat, DPAPI-encrypted.
/// </summary>
/// <remarks>
/// Writes go through a temp file and an atomic move, matching
/// <see cref="Settings.SettingsStore"/>: a crash mid-write must not leave a
/// truncated blob where a valid session used to be.
/// </remarks>
public sealed class EncryptedSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();

    public EncryptedSessionStore(string? path = null, IDataProtector? protector = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCounter", "session.dat");
        _protector = protector ?? new DpapiDataProtector();
    }

    public string FilePath => _path;

    public OAuthSession? Read()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                    return null;

                var plaintext = _protector.Unprotect(File.ReadAllBytes(_path));
                if (plaintext is null)
                {
                    Log.Warn("Stored session could not be decrypted; treating as signed out.");
                    return null;
                }

                var session = JsonSerializer.Deserialize<OAuthSession>(
                    Encoding.UTF8.GetString(plaintext), Json);

                if (session is null)
                    return null;
                if (session.Version != OAuthSession.CurrentVersion)
                {
                    Log.Warn($"Stored session has version {session.Version}; expected " +
                             $"{OAuthSession.CurrentVersion}. Treating as signed out.");
                    return null;
                }
                if (string.IsNullOrWhiteSpace(session.AccessToken) ||
                    string.IsNullOrWhiteSpace(session.RefreshToken))
                    return null;

                return session;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read the stored session: {e.Message}");
            return null;
        }
    }

    public void Write(OAuthSession session)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session, Json));
            var ciphertext = _protector.Protect(plaintext);

            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, ciphertext);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);          // no-op when absent
                File.Delete(_path + ".tmp");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not delete the stored session: {e.Message}");
            }
        }
    }
}

/// <summary>Test double.</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private OAuthSession? _session;

    public InMemorySessionStore(OAuthSession? initial = null) => _session = initial;

    public int Writes { get; private set; }
    public int Clears { get; private set; }

    public OAuthSession? Read() => _session;

    public void Write(OAuthSession session)
    {
        _session = session;
        Writes++;
    }

    public void Clear()
    {
        _session = null;
        Clears++;
    }
}

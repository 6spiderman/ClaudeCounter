using System.Text;
using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

/// <summary>
/// EncryptedSessionStore with the real Windows DPAPI protector. Everything
/// else about the store is tested platform-neutrally in
/// ClaudeCounter.Core.Tests (SessionStoreTests).
/// </summary>
public class DpapiSessionStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("cc-session-dpapi");

    private string PathFor(string name) => Path.Combine(_dir.FullName, name);

    private static readonly OAuthSession Sample = new(
        "access-token", "refresh-token",
        new DateTimeOffset(2026, 6, 11, 15, 0, 0, TimeSpan.Zero),
        "user:profile",
        new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TokensAreNotReadableInTheFileWhenEncrypted()
    {
        // With the real protector the token must not sit in the file as plain
        // text. This is the whole reason the file is encrypted.
        var store = new EncryptedSessionStore(PathFor("real.dat"), new DpapiDataProtector());
        store.Write(Sample);

        var raw = File.ReadAllBytes(PathFor("real.dat"));
        Assert.DoesNotContain("access-token", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("refresh-token", Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void RealDpapiRoundTrips()
    {
        var store = new EncryptedSessionStore(PathFor("real2.dat"), new DpapiDataProtector());
        store.Write(Sample);

        var read = store.Read();
        Assert.NotNull(read);
        Assert.Equal(Sample.AccessToken, read!.AccessToken);
        Assert.Equal(Sample.RefreshToken, read.RefreshToken);
    }

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}

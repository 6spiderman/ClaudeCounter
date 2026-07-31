using System.Text;
using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

public class SessionStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("cc-session");

    private string PathFor(string name = "session.dat") => Path.Combine(_dir.FullName, name);

    private static readonly OAuthSession Sample = new(
        "access-token", "refresh-token",
        new DateTimeOffset(2026, 6, 11, 15, 0, 0, TimeSpan.Zero),
        "user:profile",
        new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));

    /// <summary>No-op protector so the store can be tested without DPAPI.</summary>
    private sealed class PassthroughProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[]? Unprotect(byte[] ciphertext) => ciphertext;
    }

    private sealed class FailingProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[]? Unprotect(byte[] ciphertext) => null;
    }

    private EncryptedSessionStore Store(IDataProtector? protector = null, string name = "session.dat") =>
        new(PathFor(name), protector ?? new PassthroughProtector());

    [Fact]
    public void RoundTripsEveryField()
    {
        var store = Store();
        store.Write(Sample);

        var read = store.Read();
        Assert.NotNull(read);
        Assert.Equal(Sample.AccessToken, read!.AccessToken);
        Assert.Equal(Sample.RefreshToken, read.RefreshToken);
        Assert.Equal(Sample.ExpiresAt, read.ExpiresAt);
        Assert.Equal(Sample.Scopes, read.Scopes);
        Assert.Equal(Sample.ObtainedAt, read.ObtainedAt);
    }

    [Fact]
    public void MissingFileReadsAsNoSession() => Assert.Null(Store().Read());

    [Fact]
    public void UndecryptableFileReadsAsNoSession()
    {
        // The normal case for a portable copy carried to another PC: DPAPI keys
        // are machine-bound. It must read as "signed out", not throw.
        Store().Write(Sample);
        Assert.Null(Store(new FailingProtector()).Read());
    }

    [Fact]
    public void GarbageBytesReadAsNoSession()
    {
        File.WriteAllBytes(PathFor(), [0x00, 0xFF, 0x13, 0x37]);
        Assert.Null(Store().Read());
    }

    [Fact]
    public void TruncatedJsonReadsAsNoSession()
    {
        File.WriteAllBytes(PathFor(), Encoding.UTF8.GetBytes("""{"AccessToken":"a","""));
        Assert.Null(Store().Read());
    }

    [Fact]
    public void UnknownVersionReadsAsNoSession()
    {
        File.WriteAllBytes(PathFor(), Encoding.UTF8.GetBytes(
            """{"AccessToken":"a","RefreshToken":"r","ExpiresAt":"2026-06-11T15:00:00+00:00","ObtainedAt":"2026-06-11T12:00:00+00:00","Version":99}"""));

        Assert.Null(Store().Read());
    }

    [Theory]
    [InlineData("""{"AccessToken":"","RefreshToken":"r","ExpiresAt":"2026-06-11T15:00:00+00:00","ObtainedAt":"2026-06-11T12:00:00+00:00","Version":1}""")]
    [InlineData("""{"AccessToken":"a","RefreshToken":"","ExpiresAt":"2026-06-11T15:00:00+00:00","ObtainedAt":"2026-06-11T12:00:00+00:00","Version":1}""")]
    public void BlankTokensReadAsNoSession(string json)
    {
        File.WriteAllBytes(PathFor(), Encoding.UTF8.GetBytes(json));
        Assert.Null(Store().Read());
    }

    [Fact]
    public void WriteLeavesNoTempFileBehind()
    {
        Store().Write(Sample);
        Assert.False(File.Exists(PathFor() + ".tmp"), "the atomic write left its temp file behind");
    }

    [Fact]
    public void WriteCreatesMissingDirectories()
    {
        var nested = Path.Combine(_dir.FullName, "a", "b", "session.dat");
        new EncryptedSessionStore(nested, new PassthroughProtector()).Write(Sample);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void WriteReplacesAnExistingSession()
    {
        var store = Store();
        store.Write(Sample);
        store.Write(Sample with { AccessToken = "second" });

        Assert.Equal("second", store.Read()!.AccessToken);
    }

    [Fact]
    public void ClearRemovesTheSessionAndIsIdempotent()
    {
        var store = Store();
        store.Write(Sample);

        store.Clear();
        Assert.Null(store.Read());

        store.Clear();   // must not throw on an already-absent file
        Assert.Null(store.Read());
    }

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

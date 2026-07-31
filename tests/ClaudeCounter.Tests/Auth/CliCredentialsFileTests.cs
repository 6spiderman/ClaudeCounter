using System.Security.Cryptography;
using ClaudeCounter.Core.Auth;
using Xunit;

namespace ClaudeCounter.Tests.Auth;

public class CliCredentialsFileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("cc-creds");

    private string Write(string json)
    {
        var path = Path.Combine(_dir.FullName, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ParsesTokensAndExpiry()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(3);
        var path = Write($$"""
        {
          "claudeAiOauth": {
            "accessToken": "at",
            "refreshToken": "rt",
            "expiresAt": {{expiresAt.ToUnixTimeMilliseconds()}},
            "scopes": ["user:profile"],
            "subscriptionType": "max"
          }
        }
        """);

        var ok = Assert.IsType<CredentialsRead.Ok>(new CliCredentialsFile(path).Read());
        Assert.Equal("at", ok.Value.AccessToken);
        Assert.Equal("rt", ok.Value.RefreshToken);
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(), ok.Value.ExpiresAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void MissingFileIsReportedAsMissing() =>
        Assert.IsType<CredentialsRead.Missing>(
            new CliCredentialsFile(Path.Combine(_dir.FullName, "nope.json")).Read());

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]                                       // no claudeAiOauth
    [InlineData("""{"claudeAiOauth":{}}""")]                 // no accessToken
    [InlineData("""{"claudeAiOauth":{"accessToken":""}}""")] // blank accessToken
    [InlineData("""{"claudeAiOauth":{"accessToken":123}}""")]// wrong type
    public void UnusableContentIsReportedAsMalformed(string json) =>
        Assert.IsType<CredentialsRead.Malformed>(new CliCredentialsFile(Write(json)).Read());

    [Fact]
    public void AbsentExpiryIsAllowed()
    {
        var ok = Assert.IsType<CredentialsRead.Ok>(
            new CliCredentialsFile(Write("""{"claudeAiOauth":{"accessToken":"at"}}""")).Read());

        Assert.Null(ok.Value.ExpiresAt);
        Assert.Null(ok.Value.RefreshToken);
    }

    [Fact]
    public void ReadingNeverModifiesTheFile()
    {
        // The point of this whole class. ClaudeCounter must not touch Claude
        // Code's credentials: rotating its refresh token would force the CLI to
        // run /login. If a Write path is ever added here, this fails.
        var path = Write("""
        {
          "claudeAiOauth": {
            "accessToken": "at",
            "refreshToken": "rt",
            "expiresAt": 1780000000000,
            "scopes": ["user:profile"]
          },
          "somethingElse": { "keep": "me" }
        }
        """);

        var before = SHA256.HashData(File.ReadAllBytes(path));
        var writtenBefore = File.GetLastWriteTimeUtc(path);

        for (var i = 0; i < 5; i++)
            new CliCredentialsFile(path).Read();

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void HasNoWriteApiAtAll()
    {
        // Structural guard: someone adding "just a small write helper" here is
        // reintroducing the refresh-token race this design exists to remove.
        var writeMethods = typeof(CliCredentialsFile)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(writeMethods);
    }

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}

// tests/ClaudeCounter.Tests/ClaudeBackupTests/SecretDenylistTests.cs
using ClaudeBackup;
using Xunit;

namespace ClaudeCounter.Tests.Backup;

public class SecretDenylistTests
{
    [Theory]
    [InlineData(".credentials.json")]
    [InlineData("sub/.credentials.json")]
    [InlineData("auth.token")]
    [InlineData("id_rsa.key")]
    [InlineData("cert.pem")]
    [InlineData("session.dat")]
    [InlineData("MY_SECRET.txt")]
    public void FlagsSecrets(string path) => Assert.True(SecretDenylist.IsSecret(path));

    [Theory]
    [InlineData("settings.json")]
    [InlineData("CLAUDE.md")]
    [InlineData("commands/foo.md")]
    public void AllowsNonSecrets(string path) => Assert.False(SecretDenylist.IsSecret(path));

    [Fact]
    public void OffendersListsOnlySecrets()
    {
        var bad = SecretDenylist.Offenders(new[] { "settings.json", ".credentials.json" });
        Assert.Equal(new[] { ".credentials.json" }, bad);
    }

    // BackupConfig.Default().Include ships "plugins/**/*.json", and plugin /
    // MCP server config files commonly embed inline API keys. These prove
    // which plugin-config-shaped file names the NAME-based denylist actually
    // catches: names that themselves look like a secret.
    [Theory]
    [InlineData("plugins/my-server/api_token.json")]
    [InlineData("plugins/my-server/api-key.json")]
    [InlineData("plugins/my-server/apikey.json")]
    [InlineData("plugins/my-server/credentials.json")]
    [InlineData("plugins/my-server/oauth_secret.json")]
    public void FlagsPluginConfigFilesWithSecretShapedNames(string path) =>
        Assert.True(SecretDenylist.IsSecret(path));

    // Honest gap, called out in SecretDenylist's own doc comment: this is a
    // NAME-based filter, not a content scanner. A plugin or MCP config file
    // with an innocuous name that embeds an API key inline - a very common
    // real-world shape for these files - is NOT caught here. Callers must
    // not treat this denylist as a substitute for content inspection.
    [Theory]
    [InlineData("plugins/my-server/mcp.json")]
    [InlineData("plugins/my-server/config.json")]
    [InlineData("plugins/my-server/manifest.json")]
    public void DoesNotFlagInnocuouslyNamedPluginConfigsEvenIfTheyEmbedSecrets(string path) =>
        Assert.False(SecretDenylist.IsSecret(path));
}

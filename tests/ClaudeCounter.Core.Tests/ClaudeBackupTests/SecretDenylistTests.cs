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

    // Plugin/MCP server config files commonly embed inline API keys (this
    // was true of "plugins/**/*.json"; the default now ships "plugins/*.json"
    // instead - see task-3-report.md Fix round 1 - but a user can still
    // opt back into the deeper glob, so the plugin-config-shaped cases below
    // still matter). These prove which plugin-config-shaped file names the
    // NAME-based denylist actually catches: names that themselves look like
    // a secret.
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
    //
    // NOTE for future contributors: this test documents a real, current gap.
    // If a future change legitimately tightens the denylist (e.g. adding
    // content inspection, or a broader name heuristic) and one of these
    // cases starts getting flagged, that is a GOOD outcome - update or
    // remove the affected InlineData rather than treating this green test as
    // a requirement to keep the gap open.
    [Theory]
    [InlineData("plugins/my-server/mcp.json")]
    [InlineData("plugins/my-server/config.json")]
    [InlineData("plugins/my-server/manifest.json")]
    public void DoesNotFlagInnocuouslyNamedPluginConfigsEvenIfTheyEmbedSecrets(string path) =>
        Assert.False(SecretDenylist.IsSecret(path));

    // I1 (Fix round 1): cosmetic dodges around the exact-name check. Windows
    // itself ignores trailing spaces/dots when resolving a path, and an NTFS
    // alternate data stream tacks a ":stream" suffix onto a real file name -
    // none of these should let a decorated "session.dat" slip past.
    [Theory]
    [InlineData("session.dat ")]
    [InlineData("session.dat.")]
    [InlineData("session.dat:hidden")]
    [InlineData("sub/session.dat:hidden")]
    public void FlagsDecoratedExactNameMatches(string path) => Assert.True(SecretDenylist.IsSecret(path));

    // I5 (Fix round 1): common secret carriers the original list missed.
    // known_hosts added in Fix round 2 - it is technically host-inventory
    // recon (public host keys) rather than key material, but nothing in a
    // Claude config backup legitimately needs it and the false-drop risk of
    // flagging it is nil.
    [Theory]
    [InlineData(".env")]
    [InlineData(".netrc")]
    [InlineData("_netrc")]
    [InlineData(".npmrc")]
    [InlineData(".git-credentials")]
    [InlineData(".pgpass")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("id_ecdsa")]
    [InlineData("id_dsa")]
    [InlineData("known_hosts")]
    [InlineData("client.pfx")]
    [InlineData("keystore.p12")]
    [InlineData("release.jks")]
    [InlineData("app.keystore")]
    [InlineData("deploy.ppk")]
    [InlineData("key.asc")]
    [InlineData("secret.gpg")]
    public void FlagsAdditionalCommonSecretCarriers(string path) => Assert.True(SecretDenylist.IsSecret(path));

    // M4 (Fix round 1): extension matching is now dot-delimited-component
    // based, not just EndsWith, so a renamed/backed-up key/cert file is
    // still caught even though it no longer literally ends in the sensitive
    // extension.
    [Theory]
    [InlineData("cert.pem.bak")]
    [InlineData("id_rsa.key.old")]
    public void FlagsSecretExtensionEvenWhenNotTheFinalSuffix(string path) =>
        Assert.True(SecretDenylist.IsSecret(path));
}

using System.Text.Json;

namespace ClaudeCounter.Core.Auth;

public sealed record StoredOAuth(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

public abstract record CredentialsRead
{
    public sealed record Ok(StoredOAuth Value) : CredentialsRead;
    public sealed record Missing : CredentialsRead;
    public sealed record Malformed(string Message) : CredentialsRead;
}

/// <summary>
/// A <b>read-only</b> view of Claude Code's credentials file
/// (<c>~/.claude/.credentials.json</c>), used only to bootstrap a user who has
/// the CLI installed but has not signed in to ClaudeCounter yet.
/// </summary>
/// <remarks>
/// <para>
/// This class deliberately has no write path, and the refresh token it reads
/// must never be used. Anthropic rotates refresh tokens on every use and
/// invalidates the old one server-side immediately, so if ClaudeCounter
/// refreshed the CLI's chain, the CLI's next refresh would fail with
/// invalid_grant and the user would be forced to run /login. That bug is the
/// entire reason ClaudeCounter now keeps its own session.
/// </para>
/// <para>
/// If you are about to add a Write method here, don't.
/// </para>
/// </remarks>
public sealed class CliCredentialsFile
{
    private readonly string _path;

    public CliCredentialsFile(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");
    }

    public string FilePath => _path;

    public CredentialsRead Read()
    {
        string json;
        try
        {
            json = ReadWithRetry(_path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CredentialsRead.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new CredentialsRead.Malformed(e.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return new CredentialsRead.Malformed("claudeAiOauth section missing");
            if (!oauth.TryGetProperty("accessToken", out var tokenEl) ||
                tokenEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenEl.GetString()))
                return new CredentialsRead.Malformed("accessToken missing");

            var refresh = oauth.TryGetProperty("refreshToken", out var rEl) &&
                          rEl.ValueKind == JsonValueKind.String
                ? rEl.GetString()
                : null;

            DateTimeOffset? expiresAt = oauth.TryGetProperty("expiresAt", out var expEl) &&
                                        expEl.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(expEl.GetInt64())
                : null;

            return new CredentialsRead.Ok(new StoredOAuth(tokenEl.GetString()!, refresh, expiresAt));
        }
        catch (JsonException e)
        {
            return new CredentialsRead.Malformed(e.Message);
        }
    }

    // Claude Code rewrites this file when it refreshes; one short retry covers
    // an unlucky read during that write.
    private static string ReadWithRetry(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException) when (File.Exists(path))
        {
            Thread.Sleep(100);
            return File.ReadAllText(path);
        }
    }
}

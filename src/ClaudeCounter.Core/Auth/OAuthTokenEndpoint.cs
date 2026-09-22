using System.Net;
using System.Text;
using System.Text.Json;

namespace ClaudeCounter.Core.Auth;

/// <summary>
/// The only class that talks to Anthropic's token endpoint. Both grant types go
/// through <see cref="PostAsync"/>, so status mapping, expiry arithmetic and
/// exception classification exist once.
/// </summary>
public sealed class OAuthTokenEndpoint : IDisposable
{
    private const long DefaultExpiresInSeconds = 3600;

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;

    public OAuthTokenEndpoint(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _http = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <param name="fallbackRefreshToken">
    /// Used when the response omits <c>refresh_token</c>. A refresh that does
    /// not rotate keeps the caller's existing token; an authorization-code
    /// exchange has none, so it passes null and a missing token is an error.
    /// </param>
    public async Task<TokenResult> PostAsync(
        object payload, string? fallbackRefreshToken, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthConfig.Token)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // 400/401 means the grant itself was refused: a dead refresh token,
            // or a code that was already redeemed. Retrying cannot help.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return new TokenResult.Rejected(
                    $"HTTP {(int)response.StatusCode} {ErrorCode(body)}");
            if (!response.IsSuccessStatusCode)
                return new TokenResult.Transient($"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var access = GetString(root, "access_token");
            if (string.IsNullOrWhiteSpace(access))
                return new TokenResult.Transient("Response missing access_token");

            var refresh = GetString(root, "refresh_token") ?? fallbackRefreshToken;
            if (string.IsNullOrWhiteSpace(refresh))
                return new TokenResult.Transient("Response missing refresh_token");

            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var seconds)
                ? seconds
                : DefaultExpiresInSeconds;

            return new TokenResult.Success(new OAuthTokens(
                access,
                refresh,
                _now().AddSeconds(expiresIn).ToUnixTimeMilliseconds(),
                GetString(root, "scope")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TokenResult.Transient(e.Message);
        }
    }

    /// <summary>
    /// Pulls the OAuth error code out of an error body ("invalid_grant" and
    /// friends). Deliberately never returns the raw body: these messages reach
    /// the log and the UI, and an error response can echo back the credential
    /// that was rejected.
    /// </summary>
    private static string ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = GetString(doc.RootElement, "error");
            // Whitelist the shape too: an OAuth error code is a short snake_case
            // token, so anything else is not something to pass along.
            if (code is { Length: > 0 and <= 64 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return code;
        }
        catch (JsonException)
        {
            // Not JSON; fall through.
        }
        return "(no error code)";
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public void Dispose() => _http.Dispose();
}

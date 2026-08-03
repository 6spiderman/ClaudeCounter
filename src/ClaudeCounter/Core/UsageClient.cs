using System.Net;
using System.Text.Json;

namespace ClaudeCounter.Core;

public abstract record UsageResult
{
    public sealed record Success(UsageSnapshot Snapshot) : UsageResult;
    public sealed record Unauthorized : UsageResult;
    public sealed record RateLimited(TimeSpan? RetryAfter) : UsageResult;
    public sealed record TransientError(string Message) : UsageResult;
}

public sealed class UsageClient : IDisposable
{
    // CRITICAL: without a claude-code User-Agent the endpoint applies an
    // aggressively rate-limited bucket and returns persistent 429s.
    public const string UserAgent = "claude-code/2.0.0";

    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    private readonly HttpClient _http;

    public UsageClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<UsageResult> FetchAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new UsageResult.Unauthorized();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new UsageResult.RateLimited(GetRetryAfter(response));
            if (!response.IsSuccessStatusCode)
                return new UsageResult.TransientError($"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(ct);
            var snapshot = UsageJson.Parse(json);
            return snapshot is null
                ? new UsageResult.TransientError("Empty response body")
                : new UsageResult.Success(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UsageResult.TransientError(e.Message);
        }
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta;
        if (retryAfter?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }

    public void Dispose() => _http.Dispose();
}

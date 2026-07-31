using System.Text.Json;

namespace ClaudeCounter.Core;

public abstract record UpdateCheck
{
    public sealed record UpToDate : UpdateCheck;

    /// <summary>A newer release exists. <paramref name="Version"/> has no "v" prefix.</summary>
    public sealed record Available(string Version, string HtmlUrl, string? Notes) : UpdateCheck;

    /// <summary>Offline, rate-limited, or the feed made no sense. Never surfaced as an error dialog.</summary>
    public sealed record Failed(string Message) : UpdateCheck;
}

/// <summary>
/// Asks GitHub whether a newer release has been tagged. Read-only, unauthenticated,
/// and sends nothing about the user - but it is still a network call to a third
/// party, so it is behind a setting the user can turn off.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private const int MaxNoteLength = 600;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public UpdateChecker(HttpMessageHandler? handler = null, string? repoSlug = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _endpoint = new Uri(
            $"https://api.github.com/repos/{repoSlug ?? AppInfo.RepoSlug}/releases/latest");
    }

    public async Task<UpdateCheck> CheckAsync(string currentVersion, CancellationToken ct)
    {
        if (!SemVersion.TryParse(currentVersion, out var current))
            return new UpdateCheck.Failed($"Unrecognized local version '{currentVersion}'");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            // GitHub rejects requests with no User-Agent outright (403).
            request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheck.Failed($"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateCheck.Failed("Release feed had no tag_name");
            if (!SemVersion.TryParse(tag, out var latest))
                return new UpdateCheck.Failed($"Unrecognized release tag '{tag}'");

            if (latest.CompareTo(current) <= 0)
                return new UpdateCheck.UpToDate();

            var url = SafeReleaseUrl(GetString(root, "html_url"));
            return new UpdateCheck.Available(latest.ToString(), url, Truncate(GetString(root, "body")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateCheck.Failed(e.Message);
        }
    }

    /// <summary>
    /// The release feed's html_url is attacker-influenced if the feed itself is
    /// ever compromised, and it eventually reaches Process.Start via
    /// Shell.OpenUrl. Only ever hand back a genuine github.com release link;
    /// anything else - a different host, a non-https scheme, a malformed URL -
    /// falls back to the known-good releases page.
    /// </summary>
    private static string SafeReleaseUrl(string? candidate)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsoluteUri;
        }
        return AppInfo.ReleasesUrl;
    }

    private static string? Truncate(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return null;
        var trimmed = notes.Trim();
        return trimmed.Length <= MaxNoteLength ? trimmed : trimmed[..MaxNoteLength] + "...";
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public void Dispose() => _http.Dispose();
}

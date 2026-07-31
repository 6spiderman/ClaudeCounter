using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ClaudeCounter.Tests.Fakes;

/// <summary>
/// Records what was sent and replays canned responses. Serves a queue so a
/// test can exercise a retry, and keeps the last response once the queue is
/// exhausted so callers that poll do not fall off the end.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    private (HttpStatusCode Status, string Body) _last;

    public StubHandler(HttpStatusCode status, string body) => Enqueue(status, body);

    public int CallCount { get; private set; }
    public Uri? CapturedUri { get; private set; }
    public string? CapturedBody { get; private set; }
    public HttpRequestHeaders? CapturedHeaders { get; private set; }

    public StubHandler Enqueue(HttpStatusCode status, string body)
    {
        _responses.Enqueue((status, body));
        _last = (status, body);
        return this;
    }

    public string? Header(string name) =>
        CapturedHeaders is not null && CapturedHeaders.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : null;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        CapturedUri = request.RequestUri;
        CapturedHeaders = request.Headers;
        CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

        var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : _last;
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

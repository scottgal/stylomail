using System.Net;
using System.Text;

namespace StyloMail.Desktop.Tests;

/// <summary>
/// One captured request, as it was at the moment it was sent.
/// </summary>
/// <remarks>
/// A snapshot rather than the <see cref="HttpRequestMessage"/> itself, and that
/// is not tidiness. <see cref="HttpClient"/> disposes a request's content once
/// the send completes, so a handler that kept the message and let a test read
/// its body afterwards would be reading a disposed stream. Copying the body out
/// at send time is the only version of this that cannot pass for the wrong
/// reason.
/// </remarks>
internal sealed record CapturedRequest(
    HttpMethod Method,
    string Path,
    string? Query,
    IReadOnlyDictionary<string, string> Headers,
    string? Body)
{
    /// <summary>The full path including any query string, which is what a route is.</summary>
    public string PathAndQuery => Query is null ? Path : $"{Path}?{Query}";

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers every request with a
/// canned response and records what it was asked.
/// </summary>
/// <remarks>
/// This is the seam that makes the client testable without a server. It stands
/// in for the network, not for the client: every assertion in these tests is
/// about what the client put on the wire and what it made of what came back,
/// which is exactly the boundary the console has to get right.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, HttpResponseMessage> _respond;
    private readonly List<CapturedRequest> _requests = [];

    public StubHttpMessageHandler(Func<CapturedRequest, HttpResponseMessage> respond)
        => _respond = respond;

    /// <summary>Answers every request with this JSON body and status.</summary>
    public static StubHttpMessageHandler ReturningJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    /// <summary>Fails the test if it is called at all. For paths that must not reach the network.</summary>
    public static StubHttpMessageHandler Unreachable()
        => new(_ => throw new InvalidOperationException(
            "No request was expected to reach the transport."));

    public IReadOnlyList<CapturedRequest> Requests => _requests;

    public CapturedRequest LastRequest => _requests.Count > 0
        ? _requests[^1]
        : throw new InvalidOperationException("No request was made.");

    public CapturedRequest SingleRequest => _requests.Count == 1
        ? _requests[0]
        : throw new InvalidOperationException($"Expected exactly one request, saw {_requests.Count}.");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        // Content headers are a separate collection on HttpRequestMessage, and
        // Content-Type lives there. A test that asserted on the content type
        // would otherwise see nothing and conclude it was never set.
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var captured = new CapturedRequest(
            request.Method,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            request.RequestUri?.Query,
            headers,
            body);

        _requests.Add(captured);

        return _respond(captured);
    }

    /// <summary>
    /// Wraps this handler in a client pointed at an unroutable host.
    /// </summary>
    /// <remarks>
    /// <c>host.test</c> rather than <c>localhost</c>: it is in the reserved
    /// <c>.test</c> TLD, so it can never resolve to something real. A test
    /// whose stub was bypassed would fail on DNS rather than reach a service,
    /// which is the failure mode worth having.
    /// </remarks>
    public HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(this)
        {
            BaseAddress = new Uri("https://host.test"),
        };

        if (timeout is { } value)
        {
            client.Timeout = value;
        }

        return client;
    }

}

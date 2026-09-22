using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using StyloMail.Host.Serialization;
using StyloMail.Transport.Cloudflare;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>POST /v1/ingress/cloudflare</c> — inbound mail handed over by a Cloudflare Email Routing Worker.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the host's one surface that carries no principal, and the shared secret is the only
/// thing standing between it and an open mail injection endpoint.</b> The Worker has no API key and
/// no tenant claim, and Cloudflare tells us only the envelope sender and recipient it observed.
/// Authorisation is therefore two independent gates and both must hold: the Worker's secret, and a
/// recipient in a domain this deployment accepts mail for. The second is the one that still matters
/// if the first leaks.
/// </para>
/// <para>
/// Mapped only when the connector is enabled, following <see cref="SessionEndpoints"/>: a route that
/// exists but always refuses invites a configuration change to "fix" it, whereas its absence says
/// plainly that this deployment has no such intake.
/// </para>
/// <para>
/// <b>The body is the raw RFC 5322 message, not a JSON envelope with Base64 inside it.</b> The Worker
/// already holds the bytes Cloudflare gave it, and Base64 would inflate every message by a third to
/// save the caller nothing. The envelope travels in headers rather than the query string, so an
/// address needs no URL encoding and cannot be silently mangled by one.
/// </para>
/// </remarks>
public static class CloudflareIngressEndpoints
{
    public const string Route = "/v1/ingress/cloudflare";

    /// <summary>The envelope recipient Cloudflare observed. Required.</summary>
    public const string EnvelopeToHeader = "X-StyloMail-Envelope-To";

    /// <summary>The envelope sender Cloudflare observed. Optional; absent means the null sender.</summary>
    public const string EnvelopeFromHeader = "X-StyloMail-Envelope-From";

    /// <summary>
    /// Maps the route, bounded to the size the connector is configured to accept.
    /// </summary>
    /// <remarks>
    /// The body limit is raised to the connector's own maximum on purpose. Kestrel's default is
    /// smaller, so leaving it alone would mean a message between the two sizes is refused by the
    /// server before the connector ever sees it — a 413 that names no component and reads like the
    /// ingress's own limit. The two numbers have to be the same number.
    /// </remarks>
    public static void Map(IEndpointRouteBuilder app, long maxMessageBytes)
    {
        app.MapPost(Route, IngestAsync)
            .AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
            {
                var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = maxMessageBytes;
                }

                return await next(context).ConfigureAwait(false);
            });
    }

    private static async Task IngestAsync(
        HttpContext context,
        CloudflareEmailRoutingConnector connector,
        CancellationToken cancellationToken)
    {
        // An unread body is never an accepted one. The connector is deliberately not consulted —
        // there is nothing to hand it, and building a request record for a message we do not have
        // would put it through a decision path that could answer 202.
        //
        // Refused through the connector's own factory rather than by writing a status directly, so
        // that "a refusal is a 4xx" and "acceptance names a queue row" are enforced for this producer
        // as well as for the connector's own. Two ways to build one of these records would be two
        // places for the invariant to be forgotten.
        var result = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false) is not { } raw
            ? CloudflareIngressResult.Refused(400, "The message body could not be read.")
            : await connector
                .IngestAsync(
                    new CloudflareIngressRequest
                    {
                        // Never logged, never echoed, never stored: the connector compares it in
                        // constant time and discards it.
                        Authorization = context.Request.Headers.Authorization.ToString(),
                        RawMessage = raw,
                        EnvelopeFrom = Header(context, EnvelopeFromHeader),
                        EnvelopeTo = Header(context, EnvelopeToHeader),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

        context.Response.StatusCode = result.StatusCode;

        await WriteAsync(
            context,
            result.StatusCode switch
            {
                202 => "Accepted",
                503 => "Deferred",
                _ => "Refused",
            },
            result.Reason,

            // Non-null exactly on acceptance, and the caller is entitled to it: it names the durable
            // row the 202 is a claim about.
            result.QueueId,
            result.RetryAfter).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the request body, bounded.
    /// </summary>
    /// <remarks>
    /// The endpoint filter above raises the server's limit, but this read is bounded independently
    /// rather than trusting it. They are separate mechanisms, and a change to either should not
    /// quietly become an unbounded allocation. Returns null when the read failed.
    /// </remarks>
    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        try
        {
            await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException)
        {
            // A truncated body, or one the server refused for size. The client is gone or the limit
            // was enforced upstream; either way there is no message to ingest.
            return null;
        }

        return buffer.ToArray();
    }

    private static async Task WriteAsync(
        HttpContext context,
        string status,
        string reason,
        string? queueId,
        TimeSpan? retryAfter)
    {
        context.Response.ContentType = "application/json";

        if (retryAfter is { } hint)
        {
            // Read by the Worker to pace its re-offering, so it is a header rather than a body field.
            // Whole seconds: the header takes an integer.
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(hint.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await context.Response
            .WriteAsync(
                JsonSerializer.Serialize(new Response(status, reason, queueId), HostJson.Options),
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static string? Header(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;

    /// <summary>The Worker-facing response body. Deliberately tiny and content-free.</summary>
    private sealed record Response(string Status, string Reason, string? QueueId);
}

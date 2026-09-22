using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using StyloMail.Chat.Slack;
using StyloMail.Host.Chat;
using StyloMail.Host.Hosting;
using StyloMail.Host.Serialization;

namespace StyloMail.Host.Endpoints;

/// <summary>
/// <c>POST /v1/ingress/slack</c>, the platform's events arriving for assessment.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an observer, and the endpoint's shape says so.</b> A normal Slack app is told about a
/// message <em>after</em> the platform has delivered it, so nothing here can hold anything back. The
/// answer given to the platform is an acknowledgement that we have taken the event on for
/// assessment, which is why it is written durably before the answer rather than after.
/// </para>
/// <para>
/// <b>Unverifiable requests are refused before anything else reads them.</b> The body is verified
/// against the app's signing secret as the raw bytes received, because that is what the signature
/// covers; anything that normalises or re-serialises first either fails every request or compares
/// against something other than what will be parsed.
/// </para>
/// <para>
/// <b>The endpoint answers quickly and assesses off the request path.</b> A slow answer becomes a
/// platform retry, and a retry is traffic we see twice.
/// </para>
/// </remarks>
public static class SlackEventsEndpoints
{
    public const string Route = "/v1/ingress/slack";

    /// <summary>The signature the platform computed over the timestamp and the body.</summary>
    public const string SignatureHeader = "X-Slack-Signature";

    /// <summary>Part of the signed string, and checked against the clock as well.</summary>
    public const string TimestampHeader = "X-Slack-Request-Timestamp";

    /// <summary>
    /// The largest body this endpoint will accept.
    /// </summary>
    /// <remarks>
    /// A platform event is a small JSON document; this is generous for one and far below the size at
    /// which an unauthenticated surface becomes an allocation problem, which matters because the
    /// body has to be read whole before it can be verified.
    /// </remarks>
    public const long MaxBodyBytes = 256 * 1024;

    /// <summary>
    /// Maps the route, bounded to the size this endpoint will accept.
    /// </summary>
    /// <remarks>
    /// Mapped only when the connector is configured, following the Cloudflare ingress and the
    /// session endpoints: a route that exists but always refuses invites a configuration change to
    /// "fix" it, whereas its absence says plainly that this deployment has no such intake.
    /// </remarks>
    public static void Map(IEndpointRouteBuilder app, long maxBodyBytes)
    {
        app.MapPost(Route, IngestAsync)
            .AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
            {
                var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = maxBodyBytes;
                }

                return await next(context).ConfigureAwait(false);
            });
    }

    private static async Task IngestAsync(
        HttpContext context,
        IChatIntakeStore intake,
        IOptions<SlackIngressOptions> configured,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var options = configured.Value;

        var body = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var verdict = SlackSignatureVerifier.Verify(
            options.SigningSecret ?? string.Empty,
            context.Request.Headers[TimestampHeader].ToString(),
            context.Request.Headers[SignatureHeader].ToString(),
            body,
            clock.GetUtcNow());

        if (verdict != SlackSignatureVerdict.Valid)
        {
            // The verdict is a value rather than a bool precisely so this refusal can name what was
            // wrong. Nothing about the request is echoed back: a caller who cannot prove they are the
            // platform learns only that they could not.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (SlackEventReader.TryReadChallenge(body, out var challenge))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response
                .WriteAsync(JsonSerializer.Serialize(new { challenge }, HostJson.Options), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!SlackEventReader.TryRead(body, options.Identity(), out var message, out _))
        {
            // Read, understood, and not for us: our own app's post, an edit, a reaction, a shape we
            // do not assess. Acknowledged rather than refused, because refusing would make the
            // platform retry something we have already decided not to act on, and a retry is traffic
            // we would see again on every attempt.
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        // Durable before the answer. This is the whole reason the intake exists: the acknowledgement
        // below is this path's equivalent of the mail path's 250, and answering it and then losing
        // the event on a crash would be accepting a responsibility we cannot honour.
        var admission = intake.Admit(
            new ChatIntakeEntry(message.EventId, body, clock.GetUtcNow()),
            options.PendingCapacity);

        if (admission == ChatIntakeAdmission.Full)
        {
            // Refused so the platform retries, which is honest backpressure. Answering here would
            // mean acknowledging an event we did not store.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            return;
        }

        // Admitted or already known. Both are acknowledgements: an event already waiting or already
        // assessed has been taken on, and answering anything else would have the platform resend
        // something we hold.
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>
    /// Reads the body as the bytes received.
    /// </summary>
    /// <remarks>
    /// <b>Decoded as UTF-8 and never re-encoded before verification</b>, because the signature is
    /// over the bytes. A body that is not valid UTF-8 decodes to replacement characters and the
    /// signature then fails, which is the safe direction: a malformed body is refused rather than
    /// verified against something that is not what arrived.
    /// </remarks>
    private static async Task<string?> ReadBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        try
        {
            await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

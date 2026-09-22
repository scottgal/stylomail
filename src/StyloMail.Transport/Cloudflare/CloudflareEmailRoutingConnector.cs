using System.Security.Cryptography;
using System.Text;
using StyloMail.Core;
using StyloMail.Transport.Ingress;

namespace StyloMail.Transport.Cloudflare;

/// <summary>
/// Configuration for the Cloudflare Email Routing ingress.
/// </summary>
public sealed record CloudflareIngressOptions
{
    /// <summary>
    /// The secret the Worker presents. Supplied from the deployment's secret store by reference.
    /// </summary>
    /// <remarks>
    /// <b>This is a secret this deployment generates, not a provider credential.</b> Nothing here
    /// holds an OAuth token, reads a mailbox, or can act on a Cloudflare account. The distinction is
    /// the whole reason this connector was chosen: the default deployment continues to hold zero
    /// <em>provider</em> secrets, and a compromise of this value lets an attacker submit mail, which
    /// is exactly what the recipient-domain check already constrains, rather than read anyone's
    /// mailbox.
    ///
    /// <para>
    /// With no secret configured, ingest is refused entirely. An unauthenticated accept path would be
    /// a public mail injection endpoint.
    /// </para>
    /// </remarks>
    public string? SharedSecret { get; init; }

    /// <summary>Domains this deployment accepts inbound mail for. The inbound authorisation model.</summary>
    public required RecipientDomainPolicy RecipientDomains { get; init; }

    /// <summary>The tenant inbound mail is attributed to when the deployment does not remap it.</summary>
    public string InboundTenantId { get; init; } = "inbound";

    /// <summary>The identity recorded as having handed us the message.</summary>
    public string ConnectorId { get; init; } = "cloudflare-email-routing";

    public long MaxMessageBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxHops { get; init; } = 20;

    public TransportHeaderLimits HeaderLimits { get; init; } = new();

    /// <summary>Names this system is known by, for loop detection. Same meaning as the SMTP listener's.</summary>
    public IReadOnlyList<string> LocalHostIdentities { get; init; } = [];

    /// <summary>
    /// The name written into the <c>by</c> clause of the hop marker this connector adds.
    /// </summary>
    /// <remarks>
    /// Defaults to the first entry in <see cref="LocalHostIdentities"/>. It must be one of them or
    /// the loop guard will not recognise our own hop coming back, which is why a mismatch that is
    /// configured explicitly is refused rather than accepted quietly.
    /// </remarks>
    public string? ByHost { get; init; }

    /// <summary>The name the hop marker will actually carry.</summary>
    public string EffectiveByHost =>
        ByHost
        ?? LocalHostIdentities.FirstOrDefault(identity => !string.IsNullOrWhiteSpace(identity))
        ?? ConnectorId;

    /// <summary>Validates the configuration.</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(RecipientDomains);
        ArgumentNullException.ThrowIfNull(LocalHostIdentities);
        ArgumentNullException.ThrowIfNull(HeaderLimits);
        ArgumentException.ThrowIfNullOrWhiteSpace(InboundTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectorId);

        if (MaxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes), MaxMessageBytes, "Must be positive.");
        }

        if (MaxHops < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHops), MaxHops, "Must be at least 1.");
        }

        // Same silent failure the SMTP listener guards against: a `by` clause that is not one of the
        // names the loop guard recognises means a message looping back through this system is never
        // identified as a loop.
        if (ByHost is { Length: > 0 } byHost
            && LocalHostIdentities.Count > 0
            && !LocalHostIdentities.Any(identity => string.Equals(identity, byHost, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"ByHost '{byHost}' is not listed in LocalHostIdentities " +
                $"({string.Join(", ", LocalHostIdentities)}), so the hop this connector writes would " +
                "not be recognised by the loop guard.");
        }
    }
}

/// <summary>
/// One message as the Cloudflare Worker hands it to us.
/// </summary>
/// <remarks>
/// <b>There is no direction field, and that is deliberate.</b> This connector is inbound-only by
/// construction, so there is nothing for a caller to set and nothing for policy to get wrong. A
/// mutable direction would make "inbound only" a convention, and conventions do not survive a
/// refactor.
/// </remarks>
public sealed record CloudflareIngressRequest
{
    /// <summary>The <c>Authorization</c> header the Worker presented. Compared, never logged.</summary>
    public required string? Authorization { get; init; }

    /// <summary>The raw RFC 5322 message, exactly as Cloudflare delivered it.</summary>
    public required ReadOnlyMemory<byte> RawMessage { get; init; }

    /// <summary>The SMTP envelope sender Cloudflare observed. Provenance, not a header.</summary>
    public string? EnvelopeFrom { get; init; }

    /// <summary>
    /// The envelope recipient Cloudflare observed.
    /// </summary>
    /// <remarks>
    /// Taken from the envelope rather than the <c>To</c> header. The header is message content, and
    /// message content cannot decide whether we are the destination for this address, that is the
    /// check standing between us and an open inbound relay.
    /// </remarks>
    public string? EnvelopeTo { get; init; }
}

/// <summary>The HTTP-shaped answer the host should return to the Worker.</summary>
public sealed record CloudflareIngressResult
{
    public required int StatusCode { get; init; }

    public required string Reason { get; init; }

    /// <summary>Non-null exactly on acceptance.</summary>
    public string? QueueId { get; init; }

    /// <summary>How long the Worker should wait before re-offering a deferred message.</summary>
    public TimeSpan? RetryAfter { get; init; }

    // The three factories are public because a host needs to answer a request it never handed to
    // the connector, a body it could not read, a route it rejected before ingest. Hand-building the
    // record is possible but produces an unenforced combination; these are the sanctioned way to
    // construct one, and they are what keeps "202 requires a queue id" true for every producer
    // rather than only for the connector.

    /// <summary>Accepted for durable delivery. The only 202, and it names the row it is about.</summary>
    public static CloudflareIngressResult Accepted(string queueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);

        return new CloudflareIngressResult
        {
            StatusCode = 202,
            Reason = "Accepted for delivery.",
            QueueId = queueId,
        };
    }

    /// <summary>
    /// Declined for now: the Worker keeps the message and re-offers it.
    /// </summary>
    /// <remarks>
    /// <b>Never an acknowledgement.</b> Used whenever the message could not be made durable, and by
    /// the connector for any sink failure it did not classify.
    /// </remarks>
    public static CloudflareIngressResult Deferred(string reason) => new()
    {
        StatusCode = 503,
        Reason = reason,
        RetryAfter = TimeSpan.FromMinutes(1),
    };

    /// <summary>
    /// Declined permanently, before acceptance.
    /// </summary>
    /// <param name="statusCode">
    /// A 4xx. A 5xx is refused rather than accepted: the Worker treats it as retryable, so a
    /// permanent condition reported as a server error would be re-offered indefinitely.
    /// </param>
    public static CloudflareIngressResult Refused(int statusCode, string reason)
    {
        if (statusCode is >= 500 or < 400)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "A refusal is a 4xx. A 5xx would tell the Worker to retry a permanent condition.");
        }

        return new CloudflareIngressResult { StatusCode = statusCode, Reason = reason };
    }
}

/// <summary>
/// Inbound ingestion from a Cloudflare Email Routing Worker.
/// </summary>
/// <remarks>
/// <para>
/// Cloudflare Email Routing sits at the MX level and hands a Worker the raw message; the Worker posts
/// it here. There is no OAuth, no mailbox read, and no provider SDK, the connector is an
/// authenticated byte intake, which is why the default deployment needs no provider credentials at
/// all.
/// </para>
/// <para>
/// It is <b>inbound only</b>. Outbound submission goes through the SMTP listener, where the
/// authenticated principal's identity is established; there is no path from here to a send.
/// </para>
/// <para>
/// It reuses the same <see cref="ISmtpIngressSink"/> the SMTP listener uses, rather than defining a
/// second accept path. Two intake paths that could disagree about what acceptance means is precisely
/// the situation the queue's "acceptance is a queue id" rule exists to prevent, so there is one
/// sink, one decision type, and one rule about what a <c>202</c> may be built from.
/// </para>
/// </remarks>
public sealed class CloudflareEmailRoutingConnector
{
    private readonly CloudflareIngressOptions _options;
    private readonly ISmtpIngressSink _sink;
    private readonly TimeProvider _timeProvider;

    public CloudflareEmailRoutingConnector(
        CloudflareIngressOptions options,
        ISmtpIngressSink sink,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        options.Validate();

        _options = options;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Ingests one message delivered by the Worker.</summary>
    /// <remarks>
    /// Never throws for a refusal: every outcome is a status code the host can return. Only
    /// cancellation propagates.
    /// </remarks>
    public async ValueTask<CloudflareIngressResult> IngestAsync(
        CloudflareIngressRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAuthentic(request.Authorization))
        {
            // Checked before anything else is read. An unauthenticated accept path would be a public
            // mail injection endpoint, and the cost of refusing genuine traffic is a misconfigured
            // Worker secret, visible immediately, unlike injected mail.
            return CloudflareIngressResult.Refused(401, "The Worker credential was missing or invalid.");
        }

        if (request.RawMessage.Length == 0)
        {
            return CloudflareIngressResult.Refused(400, "The message body was empty.");
        }

        if (request.RawMessage.Length > _options.MaxMessageBytes)
        {
            return CloudflareIngressResult.Refused(
                413,
                $"The message is {request.RawMessage.Length} bytes, over the configured maximum.");
        }

        if (string.IsNullOrWhiteSpace(request.EnvelopeTo))
        {
            // Without an envelope recipient there is no routing decision to make, and falling back to
            // the To header would let message content choose the destination.
            return CloudflareIngressResult.Refused(
                400, "The envelope recipient was missing, so the message cannot be routed.");
        }

        if (!_options.RecipientDomains.Allows(request.EnvelopeTo))
        {
            return CloudflareIngressResult.Refused(
                403, "The recipient is not in a domain this deployment accepts mail for.");
        }

        var facts = TransportHeaderScanner.Scan(
            request.RawMessage.Span, _options.HeaderLimits, _options.LocalHostIdentities);

        if (facts.BoundExceeded is not null)
        {
            return CloudflareIngressResult.Refused(
                400, $"The message header block exceeds the configured limit ({facts.BoundExceeded}).");
        }

        if (facts.LoopDetected)
        {
            return CloudflareIngressResult.Refused(
                400, "Message loop detected: a Received header names this system as the receiving host.");
        }

        if (facts.ReceivedCount >= _options.MaxHops)
        {
            return CloudflareIngressResult.Refused(
                400, $"Too many hops: the message already carries {facts.ReceivedCount} Received headers.");
        }

        var internalMessageId = "msg_" + Guid.NewGuid().ToString("N");

        // Our hop is recorded on the way in, so that a message which loops back through us carries
        // the evidence the inbound loop guard looks for. No `from` clause: this connector has no
        // connection and therefore no connecting host or address, and inventing one would put a
        // fabricated fact into the message's permanent trace.
        var stamped = ReceivedHeader.Prepend(
            request.RawMessage.Span,
            ReceivedHeader.Build(new ReceivedHeaderStamp
            {
                ByHost = _options.EffectiveByHost,
                Protocol = "HTTPS",
                HopId = internalMessageId,
                At = _timeProvider.GetUtcNow(),
            }));

        var submission = new IngressSubmission
        {
            InternalMessageId = internalMessageId,
            TenantId = _options.InboundTenantId,
            Direction = MailDirection.Inbound,
            TrustedPrincipalId = _options.ConnectorId,
            MailFrom = request.EnvelopeFrom ?? string.Empty,
            Recipients = [request.EnvelopeTo],
            RawMessage = stamped,
            Authentication = BuildAuthenticationContext(),
            HopCount = facts.ReceivedCount,
            UntrustedMessageIdHeader = facts.UntrustedMessageId,
        };

        IngressDecision decision;

        try
        {
            decision = await _sink.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Same rule as the SMTP path: a sink failure of any kind must not become an
            // acknowledgement. A 503 makes the Worker keep the message.
            return CloudflareIngressResult.Deferred(
                $"The message could not be made durable ({ex.GetType().Name}). Not accepted.");
        }

        if (decision.Outcome == IngressOutcome.Accepted && decision.IsAcceptanceValid)
        {
            // 202, exactly as for a submission over HTTP: an acknowledgement is only meaningful
            // alongside the durable queue id it is a claim about.
            return CloudflareIngressResult.Accepted(decision.QueueId!);
        }

        if (decision.Outcome == IngressOutcome.Accepted)
        {
            return CloudflareIngressResult.Deferred(
                "The acceptance did not name a durable queue row, so responsibility was not transferred.");
        }

        // Only two outcomes remain here, and they map cleanly: a deferral makes the Worker retain the
        // message and re-offer it, and a rejection is permanent so it must not. The previous form
        // derived the HTTP status from the decision's SMTP code and could emit a 503, which would
        // have told the Worker to retry a permanent refusal, and, now that `Refused` insists on a
        // 4xx, would have thrown instead of answering.
        return decision.Outcome == IngressOutcome.Deferred
            ? CloudflareIngressResult.Deferred(decision.Reason ?? "Deferred.")
            : CloudflareIngressResult.Refused(400, decision.Reason ?? "Refused.");
    }

    /// <summary>
    /// Verifies the Worker's credential in constant time.
    /// </summary>
    /// <remarks>
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> rather than <c>string.Equals</c>: an
    /// early-exit comparison leaks the length of the matching prefix through timing, which is enough
    /// to recover a secret one byte at a time given enough attempts.
    ///
    /// <para>
    /// The expected value is never rendered into a message, and the comparison result is the only
    /// thing that escapes this method.
    /// </para>
    /// </remarks>
    private bool IsAuthentic(string? authorization)
    {
        if (string.IsNullOrEmpty(_options.SharedSecret) || string.IsNullOrWhiteSpace(authorization))
        {
            return false;
        }

        var presented = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : authorization.Trim();

        var expectedBytes = Encoding.UTF8.GetBytes(_options.SharedSecret);
        var presentedBytes = Encoding.UTF8.GetBytes(presented);

        return CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }

    /// <summary>
    /// Provenance for a message from Cloudflare.
    /// </summary>
    /// <remarks>
    /// Cloudflare tells us the envelope sender it observed, and that is genuine boundary information.
    /// It is still not authentication: no SPF, DKIM or DMARC evaluation reaches us, and no
    /// <c>Authentication-Results</c> header from the message is read, a header the sender wrote is
    /// not evidence. Provenance is therefore recorded as incomplete.
    /// </remarks>
    private static AuthenticationContext BuildAuthenticationContext() => new()
    {
        ConnectingIp = null,
        AuthenticatedAccount = null,
        Results = [],
        ApprovedSenderIdentities = [],
        ProvenanceIncomplete = true,
    };
}

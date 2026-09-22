using StyloMail.Core;

namespace StyloMail.Transport.Ingress;

/// <summary>What the listener should tell the client about a message it just handed over.</summary>
public enum IngressOutcome
{
    /// <summary>A durable queue row exists. This is the only outcome that permits a <c>250</c>.</summary>
    Accepted = 0,

    /// <summary>Responsibility was declined <em>temporarily</em>. A <c>4xx</c>, and the client keeps the mail.</summary>
    Deferred = 1,

    /// <summary>Responsibility was declined permanently. A <c>5xx</c>.</summary>
    Rejected = 2,
}

/// <summary>
/// The decision to answer a client with, expressed in SMTP's terms.
/// </summary>
/// <remarks>
/// <b>Acceptance is a queue id, not a boolean</b>, the same rule the queue imposes on itself, for
/// the same reason. A decision that claims acceptance without naming the durable row it is a claim
/// about is exactly how a <c>250</c> gets sent for mail that was never stored, and the client then
/// deletes its copy. <see cref="Accepted"/> cannot be built without one.
/// </remarks>
public sealed record IngressDecision
{
    public required IngressOutcome Outcome { get; init; }

    /// <summary>The SMTP reply code to answer with: 250, 451, 550, 554.</summary>
    public required int ReplyCode { get; init; }

    /// <summary>RFC 3463 enhanced status code, e.g. <c>4.3.0</c>.</summary>
    public required string EnhancedStatusCode { get; init; }

    public string? Reason { get; init; }

    /// <summary>The durable queue id. Non-null exactly when the outcome is acceptance.</summary>
    public string? QueueId { get; init; }

    /// <summary>
    /// Whether this decision actually names the durable row it claims to be about.
    /// </summary>
    /// <remarks>
    /// The factories make an inconsistent decision unconstructible, but a record with init-only
    /// properties can still be built by hand, so every consumer that turns a decision into an
    /// acknowledgement checks this first. It is the difference between a compile-time guarantee and
    /// a runtime one, and the rule it protects is the one where being wrong destroys mail.
    /// </remarks>
    public bool IsAcceptanceValid => Outcome != IngressOutcome.Accepted || QueueId is not null;

    /// <summary>The durable queue id, or a thrown exception when there is not one.</summary>
    public string RequireQueueId() =>
        QueueId ?? throw new InvalidOperationException(
            "This decision does not name a durable queue row, so no 250 may be derived from it.");

    /// <summary>
    /// Accepted for durable delivery. The only way to produce a <c>250</c>.
    /// </summary>
    public static IngressDecision Accepted(string queueId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);

        return new IngressDecision
        {
            Outcome = IngressOutcome.Accepted,
            ReplyCode = 250,
            EnhancedStatusCode = "2.0.0",
            QueueId = queueId,
            Reason = reason,
        };
    }

    /// <summary>
    /// Declined for now: the client retains responsibility and will try again.
    /// </summary>
    /// <remarks>
    /// The correct answer whenever the message could not be made durable, disk full, an unmounted
    /// spool, a lock that would not clear. <b>Never a <c>250</c>.</b> If we accepted here the client
    /// would delete its copy of mail we cannot produce.
    /// </remarks>
    public static IngressDecision Defer(string reason) => new()
    {
        Outcome = IngressOutcome.Deferred,
        ReplyCode = 451,
        EnhancedStatusCode = "4.3.0",
        Reason = reason,
    };

    /// <summary>Declined permanently, before acceptance.</summary>
    public static IngressDecision Reject(int code, string enhancedStatusCode, string reason)
    {
        if (code is < 500 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "A rejection must be a 5xx code.");
        }

        return new IngressDecision
        {
            Outcome = IngressOutcome.Rejected,
            ReplyCode = code,
            EnhancedStatusCode = enhancedStatusCode,
            Reason = reason,
        };
    }
}

/// <summary>A caller whose credentials were verified at the boundary.</summary>
public sealed record AuthenticatedPrincipal
{
    public required string PrincipalId { get; init; }

    public required string TenantId { get; init; }

    /// <summary>
    /// Sender identities this principal is authorised to use in <c>MAIL FROM</c>.
    /// </summary>
    /// <remarks>
    /// An empty list authorises nothing. That is deliberate: "no restriction configured" and
    /// "may send as anyone" must not be the same value, or a missing configuration becomes a
    /// universal relay permission.
    /// </remarks>
    public required IReadOnlyList<string> ApprovedSenderIdentities { get; init; }

    /// <summary>
    /// Whether this principal may use <paramref name="mailFrom"/> as its envelope sender.
    /// </summary>
    /// <remarks>
    /// <b>There is no null-sender exemption, and that is a correctness rule rather than strictness.</b>
    /// <c>&lt;&gt;</c> means "this is a DSN", RFC 5321's mechanism for bounces, and this system does
    /// not originate bounces. The spec records a permanent failure and leaves it to the upstream
    /// MTA's DSN policy, so an authenticated client submitting with a null sender is either confused
    /// or probing, and permitting it is precisely the rule that lets a bounce be generated on someone
    /// else's behalf. <c>AssessmentValidation</c> enforces it as the primary rule, named
    /// <c>AssessmentRules.NullSenderNotPermitted</c>, <c>"envelope.null_sender_not_permitted"</c>,
    /// outbound only, with <c>QueueStore.ValidateSubmission</c> as the backstop behind it. This is
    /// the third component agreeing rather than the first disagreeing.
    ///
    /// <para>
    /// <b>Named rather than merely described, and flagged as the part most likely to drift.</b> This
    /// comment previously said both components "already do" it, which was true when written and went
    /// stale the moment the assessor made the rule explicit and the queue's guard became a backstop.
    /// Prose drifts silently where a test would fail, so if either identifier changes, this comment
    /// is wrong and nothing will tell you.
    /// </para>
    ///
    /// <para>
    /// <b>Inbound is unaffected.</b> A DSN being <em>delivered to</em> a mailbox arrives
    /// unauthenticated and never reaches this method, the listener's inbound path does not consult
    /// approved identities at all. The legitimate bounce case is untouched.
    /// </para>
    /// <para>
    /// An empty entry in <see cref="ApprovedSenderIdentities"/> is ignored rather than matched, so a
    /// stray blank in a configuration list cannot silently re-permit the null sender.
    /// </para>
    /// </remarks>
    public bool MaySendAs(string mailFrom) =>
        ApprovedSenderIdentities.Any(approved =>
            !string.IsNullOrWhiteSpace(approved)
            && string.Equals(approved, mailFrom, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Verifies submission credentials.
/// </summary>
/// <remarks>
/// An injected port rather than a credential store here, because the principal directory belongs to
/// the host: it owns tenants, privileges and the API keys for the HTTP surface, and a second copy of
/// that knowledge inside the transport would be a second answer to "who is this?".
///
/// <para>
/// Implementations are only ever called on an encrypted connection, the listener enforces that
/// before it asks, so a verifier never receives a credential that travelled in the clear.
/// </para>
/// </remarks>
public interface ISubmissionAuthenticator
{
    /// <summary>Verifies credentials, returning null when they do not check out.</summary>
    ValueTask<AuthenticatedPrincipal?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>A message the listener has finished reading and wants accepted.</summary>
public sealed record IngressSubmission
{
    /// <summary>StyloMail's own identifier for this message. Opaque and tenant-unique.</summary>
    public required string InternalMessageId { get; init; }

    public required string TenantId { get; init; }

    public required MailDirection Direction { get; init; }

    /// <summary>
    /// Who handed us this message. For a submission it is the authenticated principal; for inbound
    /// it is the connector identity, never anything the message supplied.
    /// </summary>
    public required string TrustedPrincipalId { get; init; }

    public required string MailFrom { get; init; }

    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>The original bytes, exactly as received.</summary>
    public required ReadOnlyMemory<byte> RawMessage { get; init; }

    public required AuthenticationContext Authentication { get; init; }

    /// <summary>Hops already taken, counted from the message's own <c>Received</c> headers.</summary>
    public required int HopCount { get; init; }

    /// <summary>The message's own <c>Message-ID</c>. <b>Untrusted</b>; never a key.</summary>
    public string? UntrustedMessageIdHeader { get; init; }
}

/// <summary>
/// What the listener does with a message once it has been read.
/// </summary>
/// <remarks>
/// <b>This is where the <c>250</c>-after-<c>DATA</c> contract is actually discharged</b>, but the
/// implementation discharges it by delegating, not by accepting.
///
/// <para>
/// <b>The assessor is the only component that accepts.</b> An implementation calls
/// <c>IMailAssessor.AssessAsync</c> with <c>AssessmentOnly = false</c> and reads the result:
/// </para>
/// <list type="bullet">
/// <item><c>MailAssessment.SubmissionId</c>, the durable queue id. Non-null exactly when a durable
/// row exists, so it alone decides whether a <c>250</c> may be sent.</item>
/// <item><c>MailAssessment.Action</c>, <c>Defer</c> and <c>Reject</c> mean responsibility was
/// declined <em>before</em> acceptance. Nothing was queued and there is nothing to undo.</item>
/// <item><c>AssessmentContext.ClientIdempotencyKey</c>, the caller's retry key, passed through
/// unchanged. An SMTP session has no client-supplied key, so this is null for both ingress paths
/// here; the transport cannot invent one, and the resulting duplicate-after-a-lost-acknowledgement
/// risk is the SMTP ambiguity the spec accepts rather than a gap to paper over.</item>
/// </list>
///
/// <para>
/// <b>Do not call the queue's accept method directly.</b> Two components accepting the same message
/// under different idempotency keys is how one message becomes two deliveries, and the queue cannot
/// dedupe what it cannot see as the same submission.
/// </para>
/// <para>
/// <b><c>MailEnvelope.PayloadReference</c> must be a real <c>spool://</c> reference.</b> The
/// assessor resolves it to run the MIME analyzer over the original bytes and to persist exactly what
/// it accepted. A placeholder that passes the durability check but resolves to nothing
/// (<c>spool://pending</c>) makes every submission defer for a reason that looks like a storage
/// fault. Non-durable references are refused outright for non-assessment-only calls; assessment-only
/// traffic carries <c>PayloadReferences.Ephemeral</c> and never reaches that guard.
/// </para>
/// <para>
/// Implementations should return a deferral rather than throwing for an expected storage failure.
/// Both ingresses also catch and defer, so a sink that fails in a way it did not classify still
/// cannot produce a successful acceptance.
/// </para>
/// </remarks>
public interface ISmtpIngressSink
{
    ValueTask<IngressDecision> SubmitAsync(IngressSubmission submission, CancellationToken cancellationToken);
}

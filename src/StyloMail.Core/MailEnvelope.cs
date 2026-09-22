namespace StyloMail.Core;

/// <summary>
/// The SMTP envelope and transport facts for one accepted message.
/// </summary>
/// <remarks>
/// Identity comes from the envelope and from trusted provenance only. Client-supplied
/// <c>From</c> headers cannot establish identity, and <see cref="UntrustedMessageIdHeader"/>
/// exists so that the untrusted value is available for correlation without ever being
/// mistaken for a key.
/// </remarks>
public sealed record MailEnvelope
{
    /// <summary>StyloMail's own identifier for this message. Opaque and tenant-unique.</summary>
    public required string InternalMessageId { get; init; }

    public required string TenantId { get; init; }

    public required MailDirection Direction { get; init; }

    /// <summary>
    /// The authenticated principal or connector that handed us this message. For outbound
    /// this is the authenticated sending account; for a trusted MTA handoff it is the
    /// connector identity. Never derived from message content.
    /// </summary>
    public required string TrustedPrincipalId { get; init; }

    /// <summary>SMTP <c>MAIL FROM</c>. May be the null sender, as in a bounce.</summary>
    public required string MailFrom { get; init; }

    /// <summary>SMTP <c>RCPT TO</c> recipients. Dispositions are persisted per recipient.</summary>
    public required IReadOnlyList<string> RcptTo { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Digest over the original message bytes. Changes to the source make cached work stale.</summary>
    public required string MimeDigest { get; init; }

    /// <summary>Reference to the durably stored original payload. The bytes are the transport artefact; analysis works on a copy.</summary>
    public required string PayloadReference { get; init; }

    /// <summary>
    /// Hops this message has already traversed, as counted at ingress from its <c>Received</c> chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nullable on purpose: <see langword="null"/> means "not observed", not zero.</b> The queue's
    /// hop-limit check reads this, and a non-nullable field defaulting to <c>0</c> would make a sink
    /// that forgot to populate it indistinguishable from a message that genuinely arrived with no
    /// prior hops — so <c>MaxHops</c> would read as an enforced backstop while never firing. Making
    /// the unknown state representable is the whole reason this field is not an <c>int</c>.
    /// </para>
    /// <para>
    /// Added 2026-09-22. Before it existed the value died at the ingress sink: <c>MailEnvelope</c> had
    /// no hop field, <c>MailAssessor.Step7Async</c> therefore had nothing to copy into
    /// <c>QueueSubmission.HopCount</c>, and the queue's check compared a permanent default.
    /// </para>
    /// </remarks>
    public int? HopCount { get; init; }

    /// <summary>
    /// The message's own <c>Message-ID</c> header.
    /// </summary>
    /// <remarks>
    /// <b>UNTRUSTED — supplied by the sender.</b> It is recorded for diagnostics and loop
    /// tracing only. It is never an idempotency key, never an identity claim, and never a
    /// deduplication guarantee: SMTP delivery is not exactly-once, and pretending otherwise
    /// hides genuine duplicate-delivery ambiguity instead of surfacing it.
    ///
    /// <para>
    /// Deliberately <b>not</b> <c>required</c>: this value lives inside the message, so a caller
    /// cannot know it before parsing. Only the MIME adapter can observe it, and it reports the
    /// value back on the envelope it returns. Requiring it on input would force callers to invent
    /// a placeholder for data they cannot yet have.
    /// </para>
    /// </remarks>
    public string? UntrustedMessageIdHeader { get; init; }
}

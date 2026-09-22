using StyloMail.Core;

namespace StyloMail.Queue;

/// <summary>
/// The seam between the queue's delivery worker and whatever actually speaks to the outside world.
/// </summary>
/// <remarks>
/// <para>
/// <b>The worker never opens a socket.</b> It leases an item, reads the payload from the spool,
/// hands it to this port, and records what comes back. Every protocol decision — SMTP, an HTTP
/// provider API, a handoff to a local MTA — lives behind the port, so the queue never learns a
/// transport and a transport never learns <c>spool://</c>.
/// </para>
/// <para>
/// The interface lives here rather than in the transport project because the <em>caller</em> owns
/// the contract: the worker is ours, the port is yours. Defining it the other way round would make
/// <c>StyloMail.Queue</c> depend on <c>StyloMail.Transport</c> and invert the dependency.
/// </para>
/// <para>
/// <b>What a port must not assume:</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Not exactly-once.</b> A message may be presented again after a temporary failure, after a
/// crash, or after an in-doubt delivery. SMTP is not exactly-once and nothing here pretends a
/// <c>Message-ID</c> closes that gap — see <see cref="DeliveryAttemptOutcome.InDoubt"/>.
/// </item>
/// <item>
/// <b>Not exclusive.</b> A lease guarantees one worker <em>should</em> be delivering an item, but a
/// worker whose lease lapsed can still be mid-call when another worker is given the same item. A
/// port may therefore be invoked concurrently for the same message, and must not corrupt itself if
/// it is.
/// </item>
/// <item>
/// <b>Not unlimited time.</b> <see cref="DeliveryRequest.ExpiresAt"/> bounds the message's whole
/// lifetime. Work that cannot finish inside it should fail fast rather than consume the budget of
/// a message that will be given up on anyway.
/// </item>
/// </list>
/// </remarks>
public interface IDeliveryPort
{
    /// <summary>
    /// Attempts delivery to <see cref="DeliveryRequest.Recipients"/> and reports one outcome each.
    /// </summary>
    /// <remarks>
    /// Should return a result for every recipient it was given. Throwing is reserved for the
    /// unexpected — an ordinary connection failure, timeout or refusal is a
    /// <see cref="DeliveryAttemptOutcome.TemporaryFailure"/> against the recipients that were not
    /// delivered, not an exception, because an exception here says nothing about which recipients
    /// were tried and would leave the queue guessing.
    /// </remarks>
    ValueTask<DeliveryPortResult> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken);
}

/// <summary>One attempt against a message's currently-pending recipients.</summary>
/// <remarks>
/// Carries the original message bytes rather than a spool reference, deliberately: the port has no
/// business knowing how the queue stores anything, and the worker already holds the spool. The
/// cost is that the payload is materialised in memory for the duration of the call, which the
/// queue's per-message size bound (<see cref="QueueOptions.MaxPayloadBytes"/>) keeps bounded.
/// </remarks>
public sealed record DeliveryRequest
{
    /// <summary>The queue item this attempt belongs to. For correlation; the port does not own it.</summary>
    public required string QueueId { get; init; }

    /// <summary>StyloMail's own message identifier, for log and DSN correlation.</summary>
    public required string InternalMessageId { get; init; }

    public required string TenantId { get; init; }

    public required MailDirection Direction { get; init; }

    /// <summary>
    /// The authenticated principal or connector that handed us this message. Identity comes from
    /// here, never from a client-supplied header — an outbound relay may need it to choose
    /// credentials or to attribute abuse.
    /// </summary>
    public required string TrustedPrincipalId { get; init; }

    /// <summary>SMTP <c>MAIL FROM</c>. May be the null sender, as in a bounce.</summary>
    public required string MailFrom { get; init; }

    /// <summary>
    /// Exactly the recipients still pending — never the whole envelope. A recipient that already
    /// delivered, or that has permanently failed, must not be sent to again.
    /// </summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>The original message bytes, unmodified. Preserved for transport and signature integrity.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// When this message stops being our responsibility.
    /// </summary>
    /// <remarks>
    /// Present so the port can size its own timeouts against the real remaining budget instead of a
    /// fixed guess. A port that always waits its own generous timeout will blow past a nearly
    /// expired message; one that always rushes will fail messages that had time to spare.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// The message's own <c>Message-ID</c> header. <b>UNTRUSTED</b> — sender-supplied, for
    /// diagnostics and loop tracing only. Never a key and never a deduplication guarantee.
    /// </summary>
    public string? UntrustedMessageIdHeader { get; init; }
}

/// <summary>What a port observed, one outcome per recipient it was asked about.</summary>
public sealed record DeliveryPortResult
{
    /// <summary>
    /// One result per recipient. Every outcome must be drawn from
    /// <see cref="DeliveryPortContract.ReportableOutcomes"/>.
    /// </summary>
    public required IReadOnlyList<RecipientDeliveryResult> Recipients { get; init; }

    /// <summary>
    /// Optional detail for the attempt history — a response code, a diagnostic, an ambiguity note.
    /// Remember that this text is stored on a security component's audit path.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Adapts a port result into the report <see cref="QueueStore.CompleteAsync"/> takes.</summary>
    /// <remarks>
    /// Exists so the worker composes the two rather than translating between them. A hand-written
    /// mapping is where a vocabulary drift would start, and the queue types are meant to flow
    /// straight through.
    /// </remarks>
    public DeliveryReport AsReport(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        return new DeliveryReport
        {
            WorkerId = workerId,
            Recipients = Recipients,
            Detail = Detail,
        };
    }
}

/// <summary>The outcomes a delivery port is permitted to report, and the rules that bind them.</summary>
public static class DeliveryPortContract
{
    /// <summary>
    /// The outcomes a port may return. Anything outside this set is recorded by the queue itself.
    /// </summary>
    /// <remarks>
    /// <b>The split is about who observed what.</b> These five are things a transport can genuinely
    /// witness on the wire. A lapsed lease, an elapsed hold, an expiry and a reviewer's decision are
    /// the queue's own events, and a port reporting one would make an elapsed timer or a human's
    /// judgement look like something seen in a protocol exchange. <see cref="QueueStore.CompleteAsync"/>
    /// refuses them.
    /// </remarks>
    public static readonly IReadOnlySet<DeliveryAttemptOutcome> ReportableOutcomes =
        new HashSet<DeliveryAttemptOutcome>
        {
            /// <summary>The upstream accepted this recipient. Responsibility has transferred.</summary>
            DeliveryAttemptOutcome.Delivered,

            /// <summary>A 4xx, timeout or connection failure. Retryable, within the message's bounds.</summary>
            DeliveryAttemptOutcome.TemporaryFailure,

            /// <summary>A 5xx. Not retryable for this recipient; the message may still deliver to others.</summary>
            DeliveryAttemptOutcome.PermanentFailure,

            /// <summary>
            /// The data was sent but no acknowledgement arrived.
            /// </summary>
            /// <remarks>
            /// <b>This is the ambiguity the system must surface rather than resolve.</b> The queue
            /// retries — a duplicate is recoverable and a silent loss is not — and records that the
            /// outcome is unknown, so the duplicate risk stays visible in the history instead of
            /// being quietly converted into a temporary failure that looks like nothing happened.
            /// </remarks>
            DeliveryAttemptOutcome.InDoubt,

            /// <summary>A relay reported a loop, or the message came back to us.</summary>
            DeliveryAttemptOutcome.HopLimitExceeded,
        };
}

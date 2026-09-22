using StyloMail.Core;

namespace StyloMail.Queue;

/// <summary>How policy admits one recipient of a message being submitted to the queue.</summary>
/// <remarks>
/// Admission is per recipient, mirroring the delivery model: policy may let one recipient through
/// while holding or quarantining another, and the queue must be able to persist that split without
/// rejecting the whole transaction. <see cref="DeliveryState"/> is Core's; the queue stores it
/// rather than defining a parallel vocabulary that would drift from policy's.
/// </remarks>
public sealed record RecipientAdmission
{
    public required string Recipient { get; init; }

    /// <summary>
    /// Initial delivery state. <see cref="DeliveryState.Queued"/> is the ordinary case;
    /// <see cref="DeliveryState.Held"/> and <see cref="DeliveryState.Quarantined"/> record a
    /// policy intervention that applies to this recipient only.
    /// </summary>
    public DeliveryState State { get; init; } = DeliveryState.Queued;

    /// <summary>
    /// Bounded re-evaluation deadline for a hold. Meaningful only when <see cref="State"/> is
    /// <see cref="DeliveryState.Held"/>.
    /// </summary>
    /// <remarks>
    /// <b>Optional, and a null is not an error.</b> Omitting it does not create an indefinite hold
    /// and does not throw: the queue applies <see cref="QueueOptions.DefaultHoldWindow"/>, because a
    /// hold is <em>defined</em> as a bounded observation window, so there is no such thing here as a
    /// hold with no deadline to fall back to. The system cannot represent the indefinite retention
    /// the spec forbids, so a missing deadline resolves to the configured bound rather than
    /// refusing the message.
    ///
    /// <para>
    /// This paragraph previously read "required when Held", which was never true of the code and
    /// misled a consumer into building a submit path around an exception that does not exist. If a
    /// caller needs a missing policy deadline to be <em>loud</em>, that belongs in the caller's
    /// validation of policy output — not inferred from the queue.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ReEvaluateBy { get; init; }
}

/// <summary>
/// A message offered to the queue for durable delivery.
/// </summary>
/// <remarks>
/// The queue, not the caller, decides the payload reference: the spool is ours, and a caller-chosen
/// reference would let a submission name a payload that was never durably written. The stored
/// <see cref="MailEnvelope"/> is produced by the queue once the bytes are safely on disk.
/// </remarks>
public sealed record QueueSubmission
{
    public required string TenantId { get; init; }

    /// <summary>StyloMail's own opaque, tenant-unique identifier for this message.</summary>
    public required string InternalMessageId { get; init; }

    public required MailDirection Direction { get; init; }

    /// <summary>
    /// The authenticated principal or connector that handed us this message. Identity comes from
    /// here, never from a client-supplied header.
    /// </summary>
    public required string TrustedPrincipalId { get; init; }

    /// <summary>
    /// SMTP <c>MAIL FROM</c>.
    /// </summary>
    /// <remarks>
    /// <b>The null sender is refused here, and that is not the same claim as "the null sender cannot
    /// occur".</b> A bounce genuinely arrives carrying <c>&lt;&gt;</c> — but it arrives at the
    /// <em>ingress</em>, where it is assessed, and never reaches this submission path. The spec
    /// records permanent failures and leaves bounce policy to the upstream MTA; we do not originate
    /// bounces, so accepting one for delivery would mean generating a bounce on someone else's
    /// behalf.
    ///
    /// <para>
    /// This property previously read "may be the null sender, as in a bounce", which was true of the
    /// wire and false of this path — a comment that contradicted the validation two files away.
    /// Worth stating which one is meant, because the next reader reconciles that contradiction in
    /// whichever direction they happen to approach from.
    /// </para>
    /// </remarks>
    public required string MailFrom { get; init; }

    /// <summary>Digest over the original bytes, used to detect a conflicting idempotent replay.</summary>
    public required string MimeDigest { get; init; }

    /// <summary>The original message bytes. Spooled before any metadata is committed.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    public required IReadOnlyList<RecipientAdmission> Recipients { get; init; }

    /// <summary>
    /// The message's own <c>Message-ID</c> header. <b>UNTRUSTED</b> — sender-supplied, recorded for
    /// diagnostics and loop tracing only. Never a key, never a deduplication guarantee.
    /// </summary>
    public string? UntrustedMessageIdHeader { get; init; }

    /// <summary>
    /// Tenant-scoped client idempotency key for HTTP submission.
    /// </summary>
    /// <remarks>
    /// A replay with the same key and the same <see cref="MimeDigest"/> returns the existing
    /// submission; the same key with a different digest is a conflict and is refused. This makes
    /// submission replay-safe, which is a different question from SMTP's delivery ambiguity — see
    /// <see cref="DeliveryAttemptOutcome.InDoubt"/>.
    /// </remarks>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Hops this message has already taken. Refused once it reaches <see cref="QueueOptions.MaxHops"/>.
    /// </summary>
    /// <remarks>
    /// <b>Nullable, and <c>null</c> means "not observed" — never encode it as 0.</b> Zero is a real
    /// observation: a message that genuinely arrived with no prior hops. "Nobody looked" is a
    /// different fact, and collapsing the two is how a backstop comes to read as enforced while
    /// never firing. That was the defect here — <c>MaxHops</c> compared a permanent 0 for as long as
    /// nothing supplied a value, and no test in this lane could see it because the tests construct
    /// their own count.
    ///
    /// <para>
    /// <c>null</c> is therefore accepted and <em>recorded as unenforced</em>: the limit cannot be
    /// checked against a count nobody took, so the submission proceeds with the absence preserved
    /// in the row for an operator to see. It is deliberately not silently treated as passing.
    /// </para>
    /// </remarks>
    public int? HopCount { get; init; }

    /// <summary>Explicit lifetime; defaults to <see cref="QueueOptions.RetryExpiry"/> from now.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Why a submission was admitted or refused.</summary>
public enum QueueAdmission
{
    /// <summary>A durable queue row now exists. This is the only outcome that permits a 250.</summary>
    Accepted = 0,

    /// <summary>An idempotent replay of a submission already held; returns the original queue id.</summary>
    DuplicateSubmission = 1,

    /// <summary>Hop limit reached — a mail loop. Refused before acceptance.</summary>
    RefusedLoopLimit = 2,

    /// <summary>Admission control: the tenant already holds its maximum number of live items.</summary>
    RefusedTenantItemLimit = 3,

    /// <summary>Admission control: the tenant's spool budget is exhausted.</summary>
    RefusedTenantByteLimit = 4,

    /// <summary>The message exceeds the maximum payload size.</summary>
    RefusedPayloadTooLarge = 5,

    /// <summary>Same idempotency key, different payload — a client error, not a delivery.</summary>
    RefusedIdempotencyConflict = 6,

    /// <summary>
    /// A null sender (<c>""</c>) on the <b>outbound</b> submission path.
    /// </summary>
    /// <remarks>
    /// Returned rather than thrown, so a declined message is distinguishable from a caller
    /// construction error and never leaves the assessor as an unhandled exception. <b>Inbound is
    /// unaffected and must not be refused</b>: a DSN delivered to one of our users is ordinary mail.
    /// </remarks>
    RefusedNullSender = 7,
}

/// <summary>
/// The outcome of offering a message to the queue.
/// </summary>
/// <remarks>
/// <b>The acceptance gate is <see cref="QueueId"/>.</b> It is non-null if and only if a durable
/// metadata row referencing a durably spooled payload exists. Deriving acceptance from the presence
/// of the queue id — rather than from a boolean set alongside it — means no future edit can report
/// success without the durable record that success is a claim about.
///
/// <para>
/// A failure to make the payload durable never produces a result at all: it raises
/// <see cref="SpoolUnavailableException"/>, which callers must translate into a temporary failure.
/// Disk full is precisely the condition under which accepting mail would destroy it.
/// </para>
/// </remarks>
public sealed record QueueAcceptResult
{
    public required QueueAdmission Admission { get; init; }

    /// <summary>The durable queue id. Non-null exactly when the message was accepted.</summary>
    public string? QueueId { get; init; }

    /// <summary>Human-readable explanation, for logs and the operator surface.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Whether delivery responsibility has transferred. The SMTP <c>250</c> / HTTP <c>202</c> gate.
    /// </summary>
    public bool IsAccepted => QueueId is not null;

    public static QueueAcceptResult Accepted(string queueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        return new QueueAcceptResult { Admission = QueueAdmission.Accepted, QueueId = queueId };
    }

    public static QueueAcceptResult Duplicate(string queueId, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        return new QueueAcceptResult
        {
            Admission = QueueAdmission.DuplicateSubmission,
            QueueId = queueId,
            Detail = detail,
        };
    }

    public static QueueAcceptResult Refused(QueueAdmission admission, string detail)
    {
        if (admission is QueueAdmission.Accepted or QueueAdmission.DuplicateSubmission)
        {
            throw new ArgumentOutOfRangeException(
                nameof(admission), admission, "A refusal must not be built from an accepting admission.");
        }

        return new QueueAcceptResult { Admission = admission, QueueId = null, Detail = detail };
    }
}

/// <summary>Where one recipient's copy of a message has got to, as the queue holds it.</summary>
/// <remarks>
/// Policy's risk and action for this recipient live in the decision ledger, not here. The queue
/// owns delivery state and nothing else, so it does not duplicate an assessment it did not make.
/// </remarks>
public sealed record QueueRecipientState
{
    public required string Recipient { get; init; }

    public required DeliveryState State { get; init; }

    public required int Attempts { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>Deadline after which a hold must be re-evaluated by policy.</summary>
    public DateTimeOffset? ReEvaluateBy { get; init; }

    /// <summary>
    /// When this recipient is next due for a delivery attempt.
    /// </summary>
    /// <remarks>
    /// Backoff is tracked per recipient, not per message. A recipient whose upstream is failing
    /// backs off on its own schedule; sharing one timer across the message would mean either
    /// retrying a failing recipient early because a different recipient came due, or holding a
    /// healthy recipient back because a broken one is in a long backoff.
    /// </remarks>
    public DateTimeOffset? NextAttemptAt { get; init; }

    public string? LastError { get; init; }

    /// <summary>Whether this recipient still owes a delivery attempt.</summary>
    public bool IsWorkable =>
        State is DeliveryState.Queued or DeliveryState.RetryScheduled or DeliveryState.Delivering;

    /// <summary>Whether this recipient should be attempted at <paramref name="now"/>.</summary>
    public bool IsDue(DateTimeOffset now) =>
        IsWorkable && (NextAttemptAt is null || NextAttemptAt <= now);
}

/// <summary>
/// A summary of a message's delivery across all its recipients.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="DeliveryState"/>. <see cref="PartiallyDelivered"/> has no
/// single-state representation, and collapsing it would mean either reporting a partial delivery as
/// a success or reporting it as a total failure — both of which hide the subset the spec requires
/// us to surface. This is a derived view, not a stored state, so it cannot drift from the
/// per-recipient rows it summarises.
/// </remarks>
public enum QueueItemOutcome
{
    /// <summary>At least one recipient still owes work (delivery, retry, hold expiry or review).</summary>
    Pending = 0,

    AllDelivered = 1,

    /// <summary>Some recipients delivered, others terminally failed. Never hidden.</summary>
    PartiallyDelivered = 2,

    AllFailed = 3,
}

/// <summary>One message as the queue holds it, with the recipients it must still serve.</summary>
public sealed record QueueItem
{
    public required string QueueId { get; init; }

    public required string TenantId { get; init; }

    /// <summary>The stored envelope, including the queue's own payload reference.</summary>
    public required MailEnvelope Envelope { get; init; }

    /// <summary>Item-level roll-up of the per-recipient states.</summary>
    public required DeliveryState State { get; init; }

    /// <summary>Total delivery attempts made across all recipients.</summary>
    public required int Attempts { get; init; }

    /// <summary>
    /// Hops recorded at acceptance, or <c>null</c> when the count was never observed.
    /// </summary>
    /// <remarks>
    /// Stored nullable rather than collapsed to <c>0</c> at rest. A message accepted without the
    /// count being observed is <em>knowable-but-unobserved</em>, and writing <c>0</c> would erase
    /// that distinction permanently — the row would claim we looked and found no prior hops. The
    /// null is the record that the loop backstop did not run for this message.
    /// </remarks>
    public required int? HopCount { get; init; }

    public required long PayloadBytes { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public string? LastError { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Non-null once the payload bytes have been deleted; the metadata row survives.</summary>
    public DateTimeOffset? PurgedAt { get; init; }

    public required IReadOnlyList<QueueRecipientState> Recipients { get; init; }

    /// <summary>
    /// Recipients that have not settled yet. May include ones still backing off — use
    /// <see cref="QueueLease.PendingRecipients"/> to decide what to actually attempt.
    /// </summary>
    public IReadOnlyList<QueueRecipientState> UnsettledRecipients =>
        [.. Recipients.Where(r => r.IsWorkable)];

    /// <summary>How the message has fared overall.</summary>
    public QueueItemOutcome Outcome
    {
        get
        {
            if (Recipients.Count == 0)
            {
                return QueueItemOutcome.Pending;
            }

            var delivered = Recipients.Count(r => r.State == DeliveryState.Delivered);
            if (delivered == Recipients.Count)
            {
                return QueueItemOutcome.AllDelivered;
            }

            // A recipient is still owed work unless it has settled as Delivered or TerminalFailure.
            // That includes holds and quarantines, which wait on a policy decision rather than on
            // a delivery attempt — the message is not finished with us just because it is not
            // currently being delivered.
            if (Recipients.Any(r => !r.IsTerminalForDelivery()))
            {
                return QueueItemOutcome.Pending;
            }

            return delivered > 0 ? QueueItemOutcome.PartiallyDelivered : QueueItemOutcome.AllFailed;
        }
    }
}

/// <summary>An exclusive, time-bounded claim on one queue item.</summary>
public sealed record QueueLease
{
    public required string QueueId { get; init; }

    public required string WorkerId { get; init; }

    /// <summary>When this claim lapses and the recovery sweep may hand the item to another worker.</summary>
    public required DateTimeOffset LeaseExpiresAt { get; init; }

    public required QueueItem Item { get; init; }

    /// <summary>
    /// The recipients this worker must attempt: those due at claim time.
    /// </summary>
    /// <remarks>
    /// Supplied by the queue rather than derived by the worker, because "due" depends on the
    /// per-recipient backoff schedule and on the injected clock. A worker that recomputed it would
    /// need the same clock and the same rules, and any divergence shows up as either a retry
    /// against a failing upstream that has not had its backoff, or a recipient quietly skipped.
    /// </remarks>
    public required IReadOnlyList<QueueRecipientState> PendingRecipients { get; init; }
}

/// <summary>
/// An event recorded against one recipient in its append-only history.
/// </summary>
/// <remarks>
/// Most of these are reported by a delivery worker, but not all: the queue records its own events
/// (a lapsed lease, an elapsed hold, an expiry, a reviewer's decision) into the same history, so
/// that a recipient's story is complete in one place and in one order. A worker may not report the
/// queue-owned values — <see cref="QueueStore.CompleteAsync"/> rejects them — because that would
/// let an elapsed timer or a policy decision be passed off as something observed on the wire.
/// </remarks>
public enum DeliveryAttemptOutcome
{
    /// <summary>The upstream accepted this recipient. Delivery responsibility has transferred.</summary>
    Delivered = 0,

    /// <summary>A 4xx or connection failure. Retryable, subject to backoff and expiry.</summary>
    TemporaryFailure = 1,

    /// <summary>A 5xx. Not retryable for this recipient.</summary>
    PermanentFailure = 2,

    /// <summary>
    /// The upstream may or may not have accepted — the acknowledgement was lost.
    /// </summary>
    /// <remarks>
    /// <b>This is the ambiguity the spec requires us to surface rather than eliminate.</b> SMTP
    /// delivery is not exactly-once. We retry (a duplicate is recoverable, a silent loss is not)
    /// and we record that the outcome is unknown, so the duplicate risk is visible in the history
    /// rather than papered over with <c>Message-ID</c> deduplication that cannot actually close it.
    /// </remarks>
    InDoubt = 3,

    /// <summary>
    /// A lease outlived its worker and was reclaimed. Whether the dead worker delivered the message
    /// is unknown — the same ambiguity as <see cref="InDoubt"/>, from a different cause.
    /// </summary>
    LeaseExpired = 4,

    /// <summary>
    /// A hold window elapsed.
    /// </summary>
    /// <remarks>
    /// <b>A policy event, never a delivery acknowledgement.</b> Nothing was delivered and nothing
    /// failed to deliver; the hold simply ran out of time and policy now owes a decision. Recording
    /// it as a delivery outcome of any kind would conflate "we decided" with "we sent".
    /// </remarks>
    HoldExpired = 5,

    /// <summary>The hop limit was reached — a mail loop.</summary>
    HopLimitExceeded = 6,

    /// <summary>
    /// The message reached its configured lifetime with delivery still unfinished.
    /// </summary>
    /// <remarks>
    /// A delivery outcome in the sense that an attempt was owed and is now abandoned — distinct
    /// from <see cref="HoldExpired"/>, where nothing was ever owed.
    /// </remarks>
    Expired = 7,

    /// <summary>A lapsed hold was decided by policy. The detail names the decision and the decider.</summary>
    HoldResolved = 8,

    /// <summary>A reviewer released a quarantined message for delivery.</summary>
    QuarantineReleased = 9,

    /// <summary>A reviewer refused a quarantined message. Retained as a record; not delivered.</summary>
    QuarantineRejected = 10,
}

/// <summary>What a reviewer decided about a quarantined message.</summary>
/// <remarks>
/// Quarantine is a hold with a different audience: a hold is a bounded observation window, whereas
/// quarantine is "retained for authenticated review, not delivered" and has no deadline. It is
/// therefore resolved by a reviewer rather than expiring, and the decision is recorded with who
/// made it — a release is a privileged act, and an audit trail that omits the actor cannot carry
/// the weight the spec puts on it.
/// </remarks>
public enum QuarantineResolution
{
    /// <summary>Release for delivery. The message goes back to the delivery pool.</summary>
    Release = 0,

    /// <summary>Refuse it permanently. The payload is retained until retention releases it.</summary>
    Reject = 1,
}

/// <summary>An existing submission found by its tenant-scoped idempotency key.</summary>
public sealed record SubmissionLookup
{
    public required string QueueId { get; init; }

    /// <summary>The digest of the payload already held. A different one is a conflict, not a replay.</summary>
    public required string MimeDigest { get; init; }

    /// <summary>The key that was looked up.</summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>Append-only record of one attempt. History is never rewritten.</summary>
public sealed record QueueAttempt
{
    public required long AttemptId { get; init; }

    public required string QueueId { get; init; }

    public required string RecipientKey { get; init; }

    public required string WorkerId { get; init; }

    public required DeliveryAttemptOutcome Outcome { get; init; }

    public string? Detail { get; init; }

    public required DateTimeOffset AttemptedAt { get; init; }

    /// <summary>Whether this attempt leaves delivery responsibility unresolved.</summary>
    public bool IsAmbiguous =>
        Outcome is DeliveryAttemptOutcome.InDoubt or DeliveryAttemptOutcome.LeaseExpired;
}

/// <summary>What a worker did for one recipient, reported back under its lease.</summary>
public sealed record RecipientDeliveryResult
{
    public required string Recipient { get; init; }

    public required DeliveryAttemptOutcome Outcome { get; init; }

    public string? Detail { get; init; }
}

/// <summary>The results of one delivery attempt against a leased item.</summary>
public sealed record DeliveryReport
{
    public required string WorkerId { get; init; }

    public required IReadOnlyList<RecipientDeliveryResult> Recipients { get; init; }

    public string? Detail { get; init; }
}

/// <summary>Whether a completion was applied to the item or only recorded against it.</summary>
public enum QueueCompletionStatus
{
    /// <summary>The lease was valid and the recipient states were updated.</summary>
    Applied = 0,

    /// <summary>
    /// The lease had already been reclaimed, so the results were <em>recorded but not applied</em>.
    /// </summary>
    /// <remarks>
    /// Not an error and not a no-op. A worker whose lease lapsed may still have delivered, so
    /// discarding its report would lose the only evidence that it did; applying it would let a
    /// superseded worker overwrite a newer attempt. Recording without applying keeps the history
    /// complete and the state authoritative — the ambiguity is preserved instead of resolved by
    /// guesswork.
    /// </remarks>
    LeaseNotHeld = 1,
}

/// <summary>The outcome of completing a lease.</summary>
public sealed record QueueCompletionResult
{
    public required QueueCompletionStatus Status { get; init; }

    /// <summary>Attempt rows appended. Results are recorded even when they are not applied.</summary>
    public required int AttemptsRecorded { get; init; }

    /// <summary>Recipients whose reported result was ignored because they had already settled.</summary>
    public required IReadOnlyList<string> SupersededRecipients { get; init; }

    public string? Detail { get; init; }
}

/// <summary>A payload whose metadata exists but whose bytes do not.</summary>
/// <remarks>
/// <b>An integrity fault, not a retryable condition.</b> Payload-before-metadata ordering makes this
/// state unreachable by design; if it is ever observed, the durability contract has been violated
/// (or the spool was tampered with) and a human must look. It is reported, never auto-repaired and
/// never silently discarded, because the bytes it names are mail that was accepted and cannot be
/// recovered.
/// </remarks>
public sealed record QueuePayloadFault
{
    public required string QueueId { get; init; }

    public required string TenantId { get; init; }

    public required string PayloadReference { get; init; }

    public required DeliveryState State { get; init; }
}

/// <summary>What one recovery sweep found and did.</summary>
public sealed record QueueRecoveryReport
{
    /// <summary>Items taken back from workers whose lease had lapsed. Delivery state is unknown.</summary>
    public required IReadOnlyList<string> ReclaimedLeases { get; init; }

    /// <summary>Items abandoned at their configured expiry and moved to <c>TerminalFailure</c>.</summary>
    public required IReadOnlyList<string> ExpiredItems { get; init; }

    /// <summary>Items stopped because they had reached the hop limit, in case a limit was lowered.</summary>
    public required IReadOnlyList<string> HopLimitExceededItems { get; init; }

    /// <summary>
    /// Holds whose window has closed and which now need a policy decision.
    /// </summary>
    /// <remarks>
    /// Reported, not acted on. An expired hold is not a delivery acknowledgement and not a failure;
    /// only policy can say whether the message is released, quarantined or dropped, so the queue
    /// hands the decision back rather than guessing at it.
    /// </remarks>
    public required IReadOnlyList<string> ExpiredHolds { get; init; }

    /// <summary>Payloads with no live metadata, left by a crash between the two commits. Swept.</summary>
    public required IReadOnlyList<string> OrphanPayloads { get; init; }

    /// <summary>
    /// Metadata referencing a payload that is gone. <b>Should always be empty</b>; anything here is
    /// an incident, not a routine report.
    /// </summary>
    public required IReadOnlyList<QueuePayloadFault> MissingPayloads { get; init; }

    /// <summary>Payloads released by retention purge during this sweep.</summary>
    public required IReadOnlyList<string> PurgedPayloads { get; init; }
}

/// <summary>What policy decided about a hold whose window has closed.</summary>
public enum HoldResolution
{
    /// <summary>Release the held recipients for delivery, consuming a hop.</summary>
    Deliver = 0,

    /// <summary>Retain for authenticated review.</summary>
    Quarantine = 1,

    /// <summary>Give up on the held recipients.</summary>
    TerminalFailure = 2,
}

/// <summary>Internal helper: recipient-state predicates that read as English at call sites.</summary>
internal static class DeliveryStateExtensions
{
    /// <summary>Terminal for delivery purposes: no further delivery attempt will be made.</summary>
    internal static bool IsTerminalForDelivery(this QueueRecipientState recipient) =>
        recipient.State is DeliveryState.Delivered or DeliveryState.TerminalFailure;
}

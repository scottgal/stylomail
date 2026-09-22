using System.Globalization;
using Microsoft.Data.Sqlite;
using StyloMail.Core;
using StyloMail.Persistence;

namespace StyloMail.Queue;

/// <summary>
/// The durable delivery queue: acceptance, lease-based claiming, per-recipient delivery state,
/// bounded retry, and crash recovery.
/// </summary>
/// <remarks>
/// <para>
/// <b>This component never sends mail.</b> It owns durability and state; delivery is a separate
/// worker that claims a lease, hands the payload to the upstream, and reports back. Nothing here
/// opens a connection to an MTA, and nothing here composes a bounce.
/// </para>
/// <para>
/// <b>The ordering contract.</b> Acceptance durably spools the payload <em>before</em> committing
/// the metadata row that references it. A crash between the two therefore leaves an orphan payload
///, sweepable, and never metadata pointing at a payload that does not exist, which would be
/// unrecoverable loss. Every path that can fail between those two steps deletes the payload it
/// orphaned.
/// </para>
/// <para>
/// <b>Acceptance is defined by the queue id.</b> <see cref="QueueAcceptResult.IsAccepted"/> is true
/// exactly when a durable row exists. A failure to make storage durable raises rather than
/// returning, so there is no code path on which "we could not store it" can be mistaken for
/// "we took responsibility for it".
/// </para>
/// </remarks>
public sealed partial class QueueStore
{
    /// <summary>
    /// Recipient key used for events that concern the whole transaction rather than one recipient.
    /// </summary>
    /// <remarks>
    /// A reclaimed lease is evidence about the <em>message</em>: the worker died at some unknown
    /// point, so no per-recipient attempt can honestly be attributed. Recording one item-level row
    /// keeps the ambiguity in the history instead of inventing per-recipient detail we never
    /// observed, and inventing it would also consume the recipients' attempt budget for failures
    /// that may never have happened.
    /// </remarks>
    private const string ItemLevelRecipient = "*";

    private const int SqliteConstraintViolation = 19;

    private readonly SqliteConnectionFactory _connections;
    private readonly SpoolStore _spool;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private Task<int>? _initialised;

    public QueueStore(SqliteConnectionFactory connections, SpoolStore spool, QueueOptions? options = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _options = options ?? new QueueOptions();
        _options.Validate();
    }

    private readonly QueueOptions _options;

    public QueueOptions Options => _options;

    /// <summary>Creates the queue schema if absent. Safe to call repeatedly.</summary>
    /// <remarks>
    /// Every public operation initialises first, so this is only needed by a host that wants schema
    /// failures at startup rather than on the first message.
    /// </remarks>
    public async Task<int> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialised is not null)
        {
            return await _initialised.ConfigureAwait(false);
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _initialised ??= CreateSchemaAsync();
            return await _initialised.ConfigureAwait(false);
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<int> CreateSchemaAsync()
    {
        await using var connection = OpenConnection();
        return QueueSchema.EnsureCreated(connection, _options.TimeProvider);
    }

    // ---------------------------------------------------------------- acceptance

    /// <summary>
    /// Durably accepts a message, or refuses it before acceptance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A returned result with <see cref="QueueAcceptResult.IsAccepted"/> false means delivery
    /// responsibility has <b>not</b> transferred and the caller must answer with a temporary
    /// failure. A thrown <see cref="SpoolUnavailableException"/> means the same thing, loudly:
    /// durable storage was unavailable, which is exactly the condition under which accepting the
    /// message would destroy it.
    /// </para>
    /// <para>
    /// The payload is spooled before the metadata transaction opens. If that transaction refuses
    /// the message, quota, or losing an idempotency race, the payload this call just wrote is
    /// deleted rather than left as debris.
    /// </para>
    /// </remarks>
    public async Task<QueueAcceptResult> AcceptAsync(
        QueueSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateSubmission(submission);

        var now = _options.TimeProvider.GetUtcNow();

        if (submission.HopCount is { } hops && hops >= _options.MaxHops)
        {
            return QueueAcceptResult.Refused(
                QueueAdmission.RefusedLoopLimit,
                $"Message declares {hops} hops, which has reached the limit of " +
                $"{_options.MaxHops}. Refused before acceptance: this is the signature of a mail loop.");
        }

        // A null count means nobody observed it, so the limit *cannot* be checked. The submission
        // proceeds, refusing on an unobserved value would reject every message until the whole
        // chain is wired, but the absence is preserved in the row rather than defaulted to 0, so
        // an operator can see that loop protection did not run for this message.

        // The null sender, direction-dependent, and an earlier version of this got it wrong in the
        // most damaging direction, refusing a legitimate inbound DSN outright while a comment
        // claimed inbound was unaffected.
        //
        // The predicate lives in Core (`SenderAddresses`) rather than here or mirrored beside it:
        // two copies differing on a case like `<>` means the assessor and the queue disagree about
        // whether the same message is a DSN, one refusing before provider spend and the other after.
        // That divergence was live in one copy before it was consolidated.
        if (SenderAddresses.IsNullSender(submission.MailFrom)
            && submission.Direction == MailDirection.Outbound)
        {
            // Returned, not thrown: this is a message we decline, and a thrown ArgumentException is
            // indistinguishable from a caller passing an empty tenant id, one is a client bug, the
            // other is ordinary policy. It also means a declined message never leaves the assessor
            // as an unhandled exception.
            return QueueAcceptResult.Refused(
                QueueAdmission.RefusedNullSender,
                "The null sender is not permitted on the outbound submission path. The spec leaves " +
                "bounce policy to the upstream MTA and we do not originate bounces, so accepting " +
                "this would mean generating a bounce on someone else's behalf. Inbound is unaffected " +
                "and is accepted: a DSN delivered to one of our users is ordinary mail.");
        }

        if (submission.Payload.Length > _options.MaxPayloadBytes)
        {
            return QueueAcceptResult.Refused(
                QueueAdmission.RefusedPayloadTooLarge,
                $"Payload is {submission.Payload.Length} bytes, over the {_options.MaxPayloadBytes} limit.");
        }

        // Cheap replay check first, so an idempotent resubmission does not spend a spool write.
        if (submission.IdempotencyKey is { Length: > 0 } key)
        {
            var existing = await FindByIdempotencyKeyAsync(submission.TenantId, key, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return Replay(existing, submission.MimeDigest);
            }
        }

        var queueId = NewQueueId();

        // Payload first. This is the ordering that makes the crash window survivable.
        var payloadReference = PayloadReferences.RequireDurable(await _spool
            .WriteAsync(submission.TenantId, queueId, submission.Payload, cancellationToken)
            .ConfigureAwait(false));

        // The queue mints this reference itself and it is durable by construction, so the assert
        // above can never fire today. It is here because the alternative, trusting that no future
        // caller ever supplies the reference, is exactly how an assessment-only input would end up
        // as mail that vanishes on restart. Refusing at acceptance is still safe; discovering it at
        // delivery time is not.

        try
        {
            var result = await CommitAcceptanceAsync(submission, queueId, payloadReference, now, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(result.QueueId, queueId, StringComparison.Ordinal))
            {
                // The result does not name the item we just spooled, so nothing will ever
                // reference these bytes:
                //
                //  * refused, quota or a hop limit, caught after the payload was already durable;
                //  * lost an idempotency race, the winning row names the winner's payload, not
                //    ours, so ours is debris even though the result reports success.
                //
                // Testing IsAccepted here instead would leak a payload on every lost race, since a
                // duplicate is an acceptance that belongs to somebody else's bytes.
                _spool.Delete(payloadReference);
            }

            return result;
        }
        catch
        {
            _spool.Delete(payloadReference);
            throw;
        }
    }

    private async Task<QueueAcceptResult> CommitAcceptanceAsync(
        QueueSubmission submission,
        string queueId,
        string payloadReference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        // Admission control runs inside the same write transaction that inserts the row. Checking
        // outside it would let two concurrent acceptances each see room for one and both take it.
        var (liveItems, liveBytes) = ReadTenantUsage(connection, transaction, submission.TenantId);

        if (liveItems >= _options.MaxQueuedItemsPerTenant)
        {
            return QueueAcceptResult.Refused(
                QueueAdmission.RefusedTenantItemLimit,
                $"Tenant '{submission.TenantId}' already holds {liveItems} undelivered items, the " +
                $"configured limit. Refused so one tenant cannot occupy the queue.");
        }

        if (liveBytes + submission.Payload.Length > _options.MaxLivePayloadBytesPerTenant)
        {
            return QueueAcceptResult.Refused(
                QueueAdmission.RefusedTenantByteLimit,
                $"Accepting {submission.Payload.Length} bytes would take tenant " +
                $"'{submission.TenantId}' to {liveBytes + submission.Payload.Length} spooled bytes, " +
                $"over its {_options.MaxLivePayloadBytesPerTenant} budget.");
        }

        var recipients = BuildRecipients(submission, now);
        var (state, nextAttemptAt) = RollUp(recipients, now);
        var expiresAt = submission.ExpiresAt ?? now + _options.RetryExpiry;

        try
        {
            InsertItem(
                connection, transaction, submission, queueId, payloadReference,
                state, nextAttemptAt, expiresAt, now);
            InsertRecipients(connection, transaction, queueId, recipients);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == SqliteConstraintViolation && submission.IdempotencyKey is { Length: > 0 })
        {
            // Lost the race to a concurrent submission with the same key. The unique index, not a
            // check-then-insert, is what makes this safe.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            var winner = await FindByIdempotencyKeyAsync(submission.TenantId, submission.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            if (winner is null)
            {
                throw;
            }

            return Replay(winner, submission.MimeDigest);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return QueueAcceptResult.Accepted(queueId);
    }

    private static QueueAcceptResult Replay(SubmissionLookup existing, string mimeDigest)
        => string.Equals(existing.MimeDigest, mimeDigest, StringComparison.Ordinal)
            ? QueueAcceptResult.Duplicate(
                existing.QueueId,
                "Identical payload already submitted under this idempotency key; returning the original queue id.")
            : QueueAcceptResult.Refused(
                QueueAdmission.RefusedIdempotencyConflict,
                $"Idempotency key '{existing.IdempotencyKey}' was already used for a different payload " +
                $"(digest {existing.MimeDigest}). This is a client error, not a delivery.");

    // ---------------------------------------------------------------- claiming

    /// <summary>
    /// Claims the next due item for a worker, under a time-bounded lease.
    /// </summary>
    /// <remarks>
    /// Only items that are within their lifetime are claimable. An item past its expiry is left
    /// alone rather than delivered, the recovery sweep surfaces it, because delivering a message
    /// whose deadline has passed is the one outcome the retry policy exists to prevent.
    /// </remarks>
    public async Task<QueueLease?> ClaimNextAsync(
        string workerId,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var now = _options.TimeProvider.GetUtcNow();
        var leaseExpiresAt = now + _options.LeaseDuration;

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        string? queueId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                UPDATE queue_item
                   SET state = $delivering,
                       lease_owner = $worker,
                       lease_expires_at = $leaseExpiresAt,
                       updated_at = $now
                 WHERE queue_id = (
                       SELECT queue_id
                         FROM queue_item
                        WHERE state IN ($queued, $retryScheduled)
                          AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
                          AND (expires_at IS NULL OR expires_at > $now)
                          AND ($tenant IS NULL OR tenant_id = $tenant)
                        ORDER BY COALESCE(next_attempt_at, created_at), created_at
                        LIMIT 1)
                   AND state IN ($queued, $retryScheduled)
                RETURNING queue_id;
                """;
            cmd.Parameters.AddWithValue("$delivering", (int)DeliveryState.Delivering);
            cmd.Parameters.AddWithValue("$queued", (int)DeliveryState.Queued);
            cmd.Parameters.AddWithValue("$retryScheduled", (int)DeliveryState.RetryScheduled);
            cmd.Parameters.AddWithValue("$worker", workerId);
            cmd.Parameters.AddWithValue("$leaseExpiresAt", ToDb(leaseExpiresAt));
            cmd.Parameters.AddWithValue("$now", ToDb(now));
            cmd.Parameters.AddWithValue("$tenant", (object?)tenantId ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            queueId = reader.Read() ? reader.GetString(0) : null;
        }

        if (queueId is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Read back inside the same transaction that claimed it, so the row cannot have moved.
        var item = ReadItem(connection, transaction, queueId, now)
            ?? throw new QueueIntegrityException(
                $"Queue item {queueId} was claimed but could not be read back in the same " +
                "transaction. The queue's own metadata is inconsistent.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new QueueLease
        {
            QueueId = queueId,
            WorkerId = workerId,
            LeaseExpiresAt = leaseExpiresAt,
            Item = item,
            PendingRecipients = [.. item.Recipients.Where(r => r.IsDue(now))],
        };
    }

    /// <summary>Opens a leased item's payload. Throws if the payload is missing (an integrity fault).</summary>
    public Stream OpenPayload(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return _spool.OpenRead(item.Envelope.PayloadReference)
            ?? throw new QueueIntegrityException(
                $"Queue item {item.QueueId} has metadata but its payload at " +
                $"'{item.Envelope.PayloadReference}' is missing. Payload-before-metadata ordering makes " +
                "this unreachable by design, so the durability contract has been violated or the spool " +
                "was altered. The message cannot be delivered and must not be silently discarded.");
    }

    // ---------------------------------------------------------------- completion

    /// <summary>
    /// Reports the results of one delivery attempt under a lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attempt history is appended <b>unconditionally</b>, including when the lease has already been
    /// reclaimed. A worker whose lease lapsed may still have delivered; discarding its report would
    /// destroy the only evidence that it did, and applying it would let a superseded worker
    /// overwrite a newer attempt. Recording without applying keeps both the history complete and
    /// the authoritative state intact.
    /// </para>
    /// <para>
    /// A reported result for a recipient that has already settled is recorded but not applied,
    /// for the same reason and is listed in
    /// <see cref="QueueCompletionResult.SupersededRecipients"/>.
    /// </para>
    /// </remarks>
    public async Task<QueueCompletionResult> CompleteAsync(
        QueueLease lease,
        DeliveryReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(report);

        if (!string.Equals(lease.WorkerId, report.WorkerId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The report is from '{report.WorkerId}' but the lease belongs to '{lease.WorkerId}'. " +
                "A mismatched pair is a caller bug, not a delivery outcome, and applying it would " +
                "attribute one worker's results to another.",
                nameof(report));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateReport(report);

        var now = _options.TimeProvider.GetUtcNow();

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (ReadItemHeader(connection, transaction, lease.QueueId) is not { } header)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return NotHeld(0, [], $"Queue item {lease.QueueId} no longer exists.");
        }

        // Ownership, not elapsed time, decides whether a report applies.
        //
        // The lease is the mutual-exclusion primitive: exactly one worker owns an item's outcome.
        // Its expiry is a *liveness heuristic* that lets the recovery sweep take work away from a
        // worker presumed dead, it is not a rule about who owns the result. So a worker that is
        // still alive and reports after its window closed, but before anyone reclaimed the item,
        // still owns its outcome, and applying that report is strictly better than discarding it:
        // discarding would mean redelivering a message we have direct evidence was delivered.
        //
        // The moment recovery reclaims the item, ownership has genuinely moved, the owner no longer
        // matches, and the late report becomes history only.
        var leaseHeld =
            header.State == DeliveryState.Delivering
            && string.Equals(header.LeaseOwner, report.WorkerId, StringComparison.Ordinal);

        var attemptsRecorded = AppendAttempts(connection, transaction, lease.QueueId, report, now);
        BumpItemAttempts(connection, transaction, lease.QueueId, attemptsRecorded);

        if (!leaseHeld)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return NotHeld(
                attemptsRecorded,
                [],
                $"Lease on {lease.QueueId} was not held by '{report.WorkerId}' at completion time. " +
                "Results recorded as history only; delivery state left untouched.");
        }

        var current = ReadRecipients(connection, transaction, lease.QueueId);
        var superseded = new List<string>();

        foreach (var result in report.Recipients)
        {
            var recipient = current.FirstOrDefault(r =>
                string.Equals(r.Recipient, result.Recipient, StringComparison.OrdinalIgnoreCase));

            if (recipient is null || !recipient.IsWorkable)
            {
                // Reported by a worker but already settled, a duplicate result, or a worker racing
                // a recovery sweep. Never let it reopen or re-settle a decided recipient.
                superseded.Add(result.Recipient);
                continue;
            }

            ApplyOutcome(connection, transaction, lease.QueueId, recipient, result, header.ExpiresAt, now);
        }

        var updated = ReadRecipients(connection, transaction, lease.QueueId);
        var (state, nextAttemptAt) = RollUp(updated, now);
        WriteItemScheduling(connection, transaction, lease.QueueId, state, nextAttemptAt, now, lastError: null);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new QueueCompletionResult
        {
            Status = QueueCompletionStatus.Applied,
            AttemptsRecorded = attemptsRecorded,
            SupersededRecipients = superseded,
            Detail = superseded.Count > 0
                ? "Some reported recipients had already settled and were recorded without being applied."
                : null,
        };
    }

    private static QueueCompletionResult NotHeld(int recorded, IReadOnlyList<string> superseded, string detail)
        => new()
        {
            Status = QueueCompletionStatus.LeaseNotHeld,
            AttemptsRecorded = recorded,
            SupersededRecipients = superseded,
            Detail = detail,
        };

    private void ApplyOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueId,
        QueueRecipientState recipient,
        RecipientDeliveryResult result,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        switch (result.Outcome)
        {
            case DeliveryAttemptOutcome.Delivered:
                UpdateRecipient(
                    connection, transaction, queueId, recipient.Recipient,
                    DeliveryState.Delivered, now, recipient.Attempts + 1, nextAttemptAt: null,
                    lastError: null, deliveredAt: now);
                break;

            case DeliveryAttemptOutcome.PermanentFailure:
            case DeliveryAttemptOutcome.HopLimitExceeded:
                UpdateRecipient(
                    connection, transaction, queueId, recipient.Recipient,
                    DeliveryState.TerminalFailure, now, recipient.Attempts + 1, nextAttemptAt: null,
                    lastError: result.Detail, deliveredAt: null);
                break;

            case DeliveryAttemptOutcome.TemporaryFailure:
            case DeliveryAttemptOutcome.InDoubt:
                ApplyRetry(connection, transaction, queueId, recipient, result, expiresAt, now);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result), result.Outcome, "Unsupported delivery outcome.");
        }
    }

    /// <summary>
    /// Schedules the next attempt, or gives up, the bounded-retry decision.
    /// </summary>
    /// <remarks>
    /// Two independent bounds apply: the per-recipient attempt count, and the message's lifetime.
    /// The lifetime check compares the <em>next</em> attempt against the expiry rather than the
    /// current time, so a message near its deadline is given up on immediately instead of being
    /// scheduled for an attempt that would never be permitted to run.
    /// </remarks>
    private void ApplyRetry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueId,
        QueueRecipientState recipient,
        RecipientDeliveryResult result,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        var attempts = recipient.Attempts + 1;
        var nextAttemptAt = now + Backoff(queueId, attempts);

        var exhausted = attempts >= _options.MaxAttemptsPerRecipient;
        var pastDeadline = expiresAt is not null && nextAttemptAt >= expiresAt.Value;

        if (exhausted || pastDeadline)
        {
            var reason = exhausted
                ? $"Gave up after {attempts} attempts (limit {_options.MaxAttemptsPerRecipient})."
                : $"No retry would fall inside the message lifetime ending {expiresAt:O}.";

            if (result.Outcome == DeliveryAttemptOutcome.InDoubt)
            {
                reason += " The last attempt was in doubt: the upstream may have accepted it.";
            }

            UpdateRecipient(
                connection, transaction, queueId, recipient.Recipient,
                DeliveryState.TerminalFailure, now, attempts, nextAttemptAt: null,
                lastError: $"{reason} Last error: {result.Detail}".TrimEnd(), deliveredAt: null);
            return;
        }

        var note = result.Outcome == DeliveryAttemptOutcome.InDoubt
            ? "In doubt: the acknowledgement was lost, so the upstream may already have accepted this " +
              "message. Retrying risks a duplicate; not retrying risks a silent loss."
            : result.Detail;

        UpdateRecipient(
            connection, transaction, queueId, recipient.Recipient,
            DeliveryState.RetryScheduled, now, attempts, nextAttemptAt, note, deliveredAt: null);
    }

    /// <summary>
    /// Exponential backoff with deterministic jitter, capped at <see cref="QueueOptions.MaxBackoff"/>.
    /// </summary>
    /// <remarks>
    /// Jitter is a pure function of the item's identity and attempt number rather than a random
    /// draw. That keeps a given item's schedule reproducible, the same item replayed in a test, or
    /// re-read after a restart, backs off identically, while still spreading items that failed at
    /// the same instant.
    /// </remarks>
    public TimeSpan Backoff(string queueId, int attempt)
    {
        var exponent = Math.Min(Math.Max(attempt - 1, 0), 20);
        var raw = _options.BaseBackoff.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, _options.MaxBackoff.TotalMilliseconds);

        var jitter = _options.BackoffJitterFraction;
        if (jitter <= 0)
        {
            return TimeSpan.FromMilliseconds(capped);
        }

        var fraction = StableFraction(queueId, attempt);
        var scaled = capped * (1 - jitter + (2 * jitter * fraction));
        var bounded = Math.Clamp(scaled, 1, _options.MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(bounded);
    }

    private static double StableFraction(string queueId, int attempt)
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            var hash = offsetBasis;
            foreach (var c in queueId)
            {
                hash = (hash ^ c) * prime;
            }

            hash = (hash ^ (uint)attempt) * prime;
            return hash / 4294967296.0;
        }
    }

    // ---------------------------------------------------------------- hold resolution

    /// <summary>
    /// Applies a policy decision to a message whose hold has expired.
    /// </summary>
    /// <remarks>
    /// The queue surfaces a lapsed hold and waits; it never resolves one itself. Releasing a hold is
    /// a policy act, and treating the deadline as an automatic release would make "we ran out of
    /// time" indistinguishable from "we decided to send it", the conflation the spec forbids.
    ///
    /// <para>
    /// <paramref name="decidedBy"/> is recorded against every recipient the decision applied to.
    /// Releasing held mail is a privileged act, and a trail that records the outcome but not the
    /// actor answers the wrong half of the question.
    /// </para>
    /// </remarks>
    public async Task<bool> ResolveHoldAsync(
        string queueId,
        HoldResolution resolution,
        string decidedBy,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var now = _options.TimeProvider.GetUtcNow();

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (ReadItemHeader(connection, transaction, queueId) is not { } header
            || !TenantMatches(header, tenantId))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var recipients = ReadRecipients(connection, transaction, queueId);
        var held = recipients.Where(r => r.State == DeliveryState.Held).ToList();
        if (held.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = resolution switch
            {
                HoldResolution.Deliver =>
                    """
                    UPDATE queue_recipient
                       SET state = $target, next_attempt_at = $now, re_evaluate_by = NULL,
                           hold_surfaced_at = NULL, last_error = NULL
                     WHERE queue_id = $queueId AND state = $held;
                    """,
                HoldResolution.Quarantine =>
                    """
                    UPDATE queue_recipient
                       SET state = $target, next_attempt_at = NULL, re_evaluate_by = NULL,
                           hold_surfaced_at = NULL
                     WHERE queue_id = $queueId AND state = $held;
                    """,
                _ =>
                    """
                    UPDATE queue_recipient
                       SET state = $target, next_attempt_at = NULL, re_evaluate_by = NULL,
                           hold_surfaced_at = NULL,
                           last_error = 'Hold resolved as a terminal failure by policy.'
                     WHERE queue_id = $queueId AND state = $held;
                    """,
            };

            cmd.Parameters.AddWithValue("$queueId", queueId);
            cmd.Parameters.AddWithValue("$held", (int)DeliveryState.Held);
            cmd.Parameters.AddWithValue("$now", ToDb(now));
            cmd.Parameters.AddWithValue("$target", (int)(resolution switch
            {
                HoldResolution.Deliver => DeliveryState.Queued,
                HoldResolution.Quarantine => DeliveryState.Quarantined,
                _ => DeliveryState.TerminalFailure,
            }));

            cmd.ExecuteNonQuery();
        }

        // Audit the decision against every recipient it applied to, naming who made it. A release
        // is a privileged act; a trail that records the outcome but not the actor cannot carry the
        // weight the spec puts on review.
        foreach (var recipient in held)
        {
            AppendAttempt(
                connection, transaction, queueId, recipient.Recipient, decidedBy,
                DeliveryAttemptOutcome.HoldResolved,
                $"Hold resolved as {resolution} by '{decidedBy}'.", now);
        }

        // Releasing a hold makes the message due now; its recipients were never attempted, so they
        // keep their full attempt budget. Quarantining or failing it simply re-rolls the item up.
        var refreshed = ReadRecipients(connection, transaction, queueId);
        var (state, nextAttemptAt) = RollUp(refreshed, now);
        WriteItemScheduling(connection, transaction, queueId, state, nextAttemptAt, now, lastError: null);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Applies a reviewer's decision to a quarantined message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quarantine has no deadline, so unlike a hold it never resolves itself, it waits for a
    /// person. This is that person's decision, and it is recorded with who made it.
    /// </para>
    /// <para>
    /// <see cref="QuarantineResolution.Reject"/> does <b>not</b> delete anything. The message stays
    /// as a record, with its payload retained until retention releases the bytes, because the
    /// audit trail of a refused message is precisely what review is for.
    /// </para>
    /// </remarks>
    public async Task<bool> ResolveQuarantineAsync(
        string queueId,
        QuarantineResolution resolution,
        string decidedBy,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var now = _options.TimeProvider.GetUtcNow();

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (ReadItemHeader(connection, transaction, queueId) is not { } header
            || !TenantMatches(header, tenantId))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var quarantined = ReadRecipients(connection, transaction, queueId)
            .Where(r => r.State == DeliveryState.Quarantined)
            .ToList();

        if (quarantined.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var target = resolution == QuarantineResolution.Release
            ? DeliveryState.Queued
            : DeliveryState.TerminalFailure;

        foreach (var recipient in quarantined)
        {
            UpdateRecipient(
                connection, transaction, queueId, recipient.Recipient,
                target, now, recipient.Attempts,
                nextAttemptAt: target == DeliveryState.Queued ? now : null,
                lastError: resolution == QuarantineResolution.Reject
                    ? $"Quarantine rejected by '{decidedBy}'."
                    : null,
                deliveredAt: null);

            AppendAttempt(
                connection, transaction, queueId, recipient.Recipient, decidedBy,
                resolution == QuarantineResolution.Release
                    ? DeliveryAttemptOutcome.QuarantineReleased
                    : DeliveryAttemptOutcome.QuarantineRejected,
                resolution == QuarantineResolution.Release
                    ? $"Quarantine released by '{decidedBy}'; returned to the delivery pool."
                    : $"Quarantine rejected by '{decidedBy}'; retained as a record, not delivered.",
                now);
        }

        var refreshed = ReadRecipients(connection, transaction, queueId);
        var (state, nextAttemptAt) = RollUp(refreshed, now);
        WriteItemScheduling(connection, transaction, queueId, state, nextAttemptAt, now, lastError: null);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Tenant-scopes an item addressed by queue id.</summary>
    private static bool TenantMatches(ItemHeader header, string? tenantId)
        => tenantId is null || string.Equals(header.TenantId, tenantId, StringComparison.Ordinal);

    /// <summary>Tenant-scopes an item addressed by queue id, reading it to find out.</summary>
    private static bool TenantMatches(
        SqliteConnection connection, SqliteTransaction? transaction, string queueId, string? tenantId)
    {
        if (tenantId is null)
        {
            return true;
        }

        return ReadItemHeader(connection, transaction, queueId) is { } header
            && TenantMatches(header, tenantId);
    }

    // ---------------------------------------------------------------- recovery

    /// <summary>
    /// The recovery sweep: reclaims dead workers' leases, gives up on expired messages, surfaces
    /// lapsed holds for policy, and maintains the spool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intended to run periodically and after a restart. It is the only path that returns a message
    /// stranded by a crash to the delivery pool, so a deployment that never runs it will hold
    /// crashed work forever.
    /// </para>
    /// <para>
    /// <b>It reports; it does not adjudicate.</b> Lapsed holds come back as work for policy rather
    /// than being released or failed, and metadata whose payload is missing comes back as a fault
    /// rather than being repaired or deleted.
    /// </para>
    /// <para>
    /// <b>Cost is linear in the queue, not in the work found.</b> The integrity and orphan passes
    /// walk every live row and every spooled file, because finding the one unreferenced file means
    /// knowing about all the referenced ones. That is the right trade for a periodic sweep and the
    /// wrong one for a hot path; it is not intended to run per message. The portions that must
    /// scale, reclaiming, expiring, hop enforcement and hold surfacing, are all indexed queries.
    /// </para>
    /// </remarks>
    public async Task<QueueRecoveryReport> RecoverAsync(
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var now = _options.TimeProvider.GetUtcNow();
        var reclaimed = new List<string>();
        var expired = new List<string>();
        var hopLimited = new List<string>();
        var holds = new List<string>();

        await using (var connection = OpenConnection())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            ReclaimExpiredLeases(connection, transaction, tenantId, now, reclaimed);

            // Runs after reclaim so an item whose lease lapsed *and* whose lifetime has passed is
            // given up on in the same sweep rather than handed straight back out.
            ExpireOverdueItems(connection, transaction, tenantId, now, expired);
            EnforceHopLimit(connection, transaction, tenantId, now, hopLimited);
            SurfaceExpiredHolds(connection, transaction, tenantId, now, holds);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var purged = await PurgeAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var missing = await FindMissingPayloadsAsync(cancellationToken).ConfigureAwait(false);
        var live = await ReadLiveReferencesAsync(cancellationToken).ConfigureAwait(false);

        // Anything still referenced by a row that has not been purged is live; anything else old
        // enough to be past the acceptance critical section is debris.
        var orphans = _spool.SweepOrphans(live, now - _options.OrphanSweepMinimumAge);

        return new QueueRecoveryReport
        {
            ReclaimedLeases = reclaimed,
            ExpiredItems = expired,
            HopLimitExceededItems = hopLimited,
            ExpiredHolds = holds,
            OrphanPayloads = orphans,
            MissingPayloads = missing,
            PurgedPayloads = purged,
        };
    }

    private static void ReclaimExpiredLeases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? tenantId,
        DateTimeOffset now,
        List<string> reclaimed)
    {
        var candidates = new List<(string QueueId, string? Owner)>();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                SELECT queue_id, lease_owner
                  FROM queue_item
                 WHERE state = $delivering
                   AND lease_expires_at IS NOT NULL
                   AND lease_expires_at <= $now
                   AND ($tenant IS NULL OR tenant_id = $tenant);
                """;
            cmd.Parameters.AddWithValue("$delivering", (int)DeliveryState.Delivering);
            cmd.Parameters.AddWithValue("$now", ToDb(now));
            cmd.Parameters.AddWithValue("$tenant", (object?)tenantId ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        foreach (var (queueId, owner) in candidates)
        {
            AppendAttempt(
                connection, transaction, queueId, ItemLevelRecipient, owner ?? "unknown",
                DeliveryAttemptOutcome.LeaseExpired,
                "Lease lapsed without a delivery acknowledgement. Whether the upstream accepted this " +
                "message is unknown: the retry may duplicate it.",
                now);

            BumpItemAttempts(connection, transaction, queueId, 1);

            var recipients = ReadRecipients(connection, transaction, queueId);
            var (state, nextAttemptAt) = RollUp(recipients, now);
            WriteItemScheduling(
                connection, transaction, queueId, state, nextAttemptAt, now,
                lastError: "Reclaimed from a worker whose lease lapsed; delivery state is unknown.");

            reclaimed.Add(queueId);
        }
    }

    private static void ExpireOverdueItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? tenantId,
        DateTimeOffset now,
        List<string> expired)
    {
        var candidates = SelectQueueIds(
            connection, transaction,
            """
            SELECT queue_id
              FROM queue_item
             WHERE state IN ($queued, $retryScheduled)
               AND expires_at IS NOT NULL
               AND expires_at <= $now
               AND ($tenant IS NULL OR tenant_id = $tenant);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$queued", (int)DeliveryState.Queued);
                cmd.Parameters.AddWithValue("$retryScheduled", (int)DeliveryState.RetryScheduled);
                cmd.Parameters.AddWithValue("$now", ToDb(now));
                cmd.Parameters.AddWithValue("$tenant", (object?)tenantId ?? DBNull.Value);
            });

        foreach (var queueId in candidates)
        {
            // Recipients that already delivered keep their Delivered state. Collapsing a partially
            // delivered message into a blanket failure would erase the deliveries that did happen.
            var recipients = ReadRecipients(connection, transaction, queueId);
            foreach (var recipient in recipients.Where(r => !r.IsTerminalForDelivery()))
            {
                AppendAttempt(
                    connection, transaction, queueId, recipient.Recipient, "recovery",
                    DeliveryAttemptOutcome.Expired,
                    "Message lifetime expired before delivery completed.", now);

                UpdateRecipient(
                    connection, transaction, queueId, recipient.Recipient,
                    DeliveryState.TerminalFailure, now, recipient.Attempts, nextAttemptAt: null,
                    lastError: "Message lifetime expired before delivery completed.", deliveredAt: null);
            }

            SetItemTerminal(
                connection, transaction, queueId, now,
                "Message lifetime expired before delivery completed.");

            expired.Add(queueId);
        }
    }

    private void EnforceHopLimit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? tenantId,
        DateTimeOffset now,
        List<string> hopLimited)
    {
        var candidates = SelectQueueIds(
            connection, transaction,
            """
            SELECT queue_id
              FROM queue_item
             -- `hop_count` NULL never satisfies this: an unobserved count cannot trip a limit.
             WHERE hop_count >= $maxHops
               AND state NOT IN ($delivered, $terminal)
               AND ($tenant IS NULL OR tenant_id = $tenant);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$maxHops", _options.MaxHops);
                cmd.Parameters.AddWithValue("$delivered", (int)DeliveryState.Delivered);
                cmd.Parameters.AddWithValue("$terminal", (int)DeliveryState.TerminalFailure);
                cmd.Parameters.AddWithValue("$tenant", (object?)tenantId ?? DBNull.Value);
            });

        foreach (var queueId in candidates)
        {
            var recipients = ReadRecipients(connection, transaction, queueId);
            foreach (var recipient in recipients.Where(r => !r.IsTerminalForDelivery()))
            {
                AppendAttempt(
                    connection, transaction, queueId, recipient.Recipient, "recovery",
                    DeliveryAttemptOutcome.HopLimitExceeded,
                    "Hop limit reached; refusing to keep this message circulating.", now);

                UpdateRecipient(
                    connection, transaction, queueId, recipient.Recipient,
                    DeliveryState.TerminalFailure, now, recipient.Attempts, nextAttemptAt: null,
                    lastError: "Hop limit reached; refusing to keep this message circulating.",
                    deliveredAt: null);
            }

            SetItemTerminal(connection, transaction, queueId, now, "Hop limit reached.");
            hopLimited.Add(queueId);
        }
    }

    /// <summary>
    /// Reports holds whose window has closed, without resolving them.
    /// </summary>
    /// <remarks>
    /// The recipient stays <see cref="DeliveryState.Held"/>. Marking it delivered, failed or
    /// released here would answer a question only policy is entitled to answer, and the whole
    /// point of the hold is that the answer is a decision, not an elapsed timer.
    /// </remarks>
    private static void SurfaceExpiredHolds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? tenantId,
        DateTimeOffset now,
        List<string> holds)
    {
        var pending = new List<(string QueueId, string Recipient)>();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                SELECT r.queue_id, r.recipient_key
                  FROM queue_recipient r
                  JOIN queue_item i ON i.queue_id = r.queue_id
                 WHERE r.state = $held
                   AND r.re_evaluate_by IS NOT NULL
                   AND r.re_evaluate_by <= $now
                   AND r.hold_surfaced_at IS NULL
                   AND ($tenant IS NULL OR i.tenant_id = $tenant);
                """;
            cmd.Parameters.AddWithValue("$held", (int)DeliveryState.Held);
            cmd.Parameters.AddWithValue("$now", ToDb(now));
            cmd.Parameters.AddWithValue("$tenant", (object?)tenantId ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                pending.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (queueId, recipient) in pending)
        {
            AppendAttempt(
                connection, transaction, queueId, recipient, "recovery",
                DeliveryAttemptOutcome.HoldExpired,
                "Hold window closed. This is a policy decision owed, not a delivery outcome.", now);

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                UPDATE queue_recipient
                   SET hold_surfaced_at = $now
                 WHERE queue_id = $queueId AND recipient_key = $recipient;
                """;
            cmd.Parameters.AddWithValue("$now", ToDb(now));
            cmd.Parameters.AddWithValue("$queueId", queueId);
            cmd.Parameters.AddWithValue("$recipient", recipient);
            cmd.ExecuteNonQuery();

            if (!holds.Contains(queueId, StringComparer.Ordinal))
            {
                holds.Add(queueId);
            }
        }
    }

    /// <summary>
    /// Releases the payload bytes of settled messages past the retention window.
    /// </summary>
    /// <remarks>
    /// The <c>purged_at</c> mark is committed <em>before</em> the file is deleted. Deleting first
    /// would, on a crash between the two, leave a row that claims a payload it no longer has, the
    /// integrity fault this component is built to avoid. Marking first means the worst case is a
    /// file with no live reference, which the orphan sweep collects on the next pass.
    /// </remarks>
    private async Task<IReadOnlyList<string>> PurgeAsync(string? tenantId, CancellationToken cancellationToken)
    {
        var now = _options.TimeProvider.GetUtcNow();
        var cutoff = now - _options.TerminalPayloadRetention;
        var candidates = new List<(string QueueId, string Reference)>();

        await using (var connection = OpenConnection())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    """
                    SELECT queue_id, payload_reference
                      FROM queue_item
                     WHERE purged_at IS NULL
                       AND state IN ($delivered, $terminal)
                       AND updated_at <= $cutoff
                       AND ($tenant IS NULL OR tenant_id = $tenant);
                    """;
                cmd.Parameters.AddWithValue("$delivered", (int)DeliveryState.Delivered);
                cmd.Parameters.AddWithValue("$terminal", (int)DeliveryState.TerminalFailure);
                cmd.Parameters.AddWithValue("$cutoff", ToDb(cutoff));
                cmd.Parameters.AddWithValue("$tenant", (object?)tenantId ?? DBNull.Value);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (queueId, _) in candidates)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE queue_item SET purged_at = $now WHERE queue_id = $queueId;";
                cmd.Parameters.AddWithValue("$now", ToDb(now));
                cmd.Parameters.AddWithValue("$queueId", queueId);
                cmd.ExecuteNonQuery();
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var purged = new List<string>();
        foreach (var (_, reference) in candidates)
        {
            _spool.Delete(reference);
            purged.Add(reference);
        }

        return purged;
    }

    /// <summary>
    /// Metadata whose payload is gone. Reported, never repaired.
    /// </summary>
    private async Task<IReadOnlyList<QueuePayloadFault>> FindMissingPayloadsAsync(CancellationToken cancellationToken)
    {
        var faults = new List<QueuePayloadFault>();

        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT queue_id, tenant_id, payload_reference, state
              FROM queue_item
             WHERE purged_at IS NULL;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var reference = reader.GetString(2);
            if (!_spool.Exists(reference))
            {
                faults.Add(new QueuePayloadFault
                {
                    QueueId = reader.GetString(0),
                    TenantId = reader.GetString(1),
                    PayloadReference = reference,
                    State = (DeliveryState)reader.GetInt32(3),
                });
            }
        }

        return faults;
    }

    private async Task<IReadOnlySet<string>> ReadLiveReferencesAsync(CancellationToken cancellationToken)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT payload_reference FROM queue_item WHERE purged_at IS NULL;";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            live.Add(reader.GetString(0));
        }

        return live;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>Reads one item, or null if it is not there or belongs to another tenant.</summary>
    /// <param name="queueId">The queue id returned by <see cref="AcceptAsync"/>.</param>
    /// <param name="tenantId">
    /// When supplied, the read is tenant-scoped and an item belonging to someone else reads as
    /// absent. A queue id is unguessable, but an operator-facing route that could be pointed at
    /// another tenant's mail by changing one path segment is not one worth writing down.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<QueueItem?> GetItemAsync(
        string queueId,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = OpenConnection();

        // A single-row read needs no transaction: it is one statement, and taking a read lock to
        // fetch it would only add contention with the workers draining the queue.
        var item = ReadItem(connection, transaction: null, queueId, _options.TimeProvider.GetUtcNow());

        return TenantMatches(item, tenantId) ? item : null;
    }

    /// <summary>
    /// Looks up an existing submission by its tenant-scoped idempotency key.
    /// </summary>
    /// <remarks>
    /// For a route that wants to answer "have I already taken this?" without re-submitting.
    /// Submission itself is idempotent without this, <see cref="AcceptAsync"/> returns the
    /// original queue id for a replay, so this is for callers that need the answer before they
    /// have a payload in hand.
    /// </remarks>
    public async Task<SubmissionLookup?> FindSubmissionAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        return await FindByIdempotencyKeyAsync(tenantId, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TenantMatches(QueueItem? item, string? tenantId)
        => item is not null
        && (tenantId is null || string.Equals(item.TenantId, tenantId, StringComparison.Ordinal));

    /// <summary>
    /// Reads the append-only history for an item, oldest first.
    /// </summary>
    /// <remarks>
    /// Covers delivery attempts and the queue's own events, a reclaimed lease, an elapsed hold, a
    /// reviewer's decision, in the order they happened. This is the record that makes a disputed
    /// delivery answerable.
    /// </remarks>
    public async Task<IReadOnlyList<QueueAttempt>> GetAttemptsAsync(
        string queueId,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var attempts = new List<QueueAttempt>();

        await using var connection = OpenConnection();

        if (!TenantMatches(connection, transaction: null, queueId, tenantId))
        {
            return attempts;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT attempt_id, queue_id, recipient_key, worker_id, outcome, detail, attempted_at
              FROM queue_attempt
             WHERE queue_id = $queueId
             ORDER BY attempt_id;
            """;
        cmd.Parameters.AddWithValue("$queueId", queueId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(new QueueAttempt
            {
                AttemptId = reader.GetInt64(0),
                QueueId = reader.GetString(1),
                RecipientKey = reader.GetString(2),
                WorkerId = reader.GetString(3),
                Outcome = Enum.Parse<DeliveryAttemptOutcome>(reader.GetString(4)),
                Detail = reader.IsDBNull(5) ? null : reader.GetString(5),
                AttemptedAt = FromDb(reader.GetString(6)) ?? DateTimeOffset.MinValue,
            });
        }

        return attempts;
    }

    /// <summary>Counts items per state for one tenant. Diagnostics and tests.</summary>
    public async Task<IReadOnlyDictionary<DeliveryState, int>> CountByStateAsync(
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var counts = new Dictionary<DeliveryState, int>();

        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT state, COUNT(*) FROM queue_item
             WHERE ($tenant IS NULL OR tenant_id = $tenant)
             GROUP BY state;
            """;
        cmd.Parameters.AddWithValue("$tenant", (object?)tenantId ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[(DeliveryState)reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    // ---------------------------------------------------------------- SQL helpers

    private static List<string> SelectQueueIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Action<SqliteCommand> bind)
    {
        var ids = new List<string>();

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        bind(cmd);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static void InsertItem(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QueueSubmission submission,
        string queueId,
        string payloadReference,
        DeliveryState state,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            INSERT INTO queue_item (
                queue_id, tenant_id, internal_message_id, payload_reference, mime_digest,
                direction, mail_from, trusted_principal_id, untrusted_message_id, payload_bytes,
                idempotency_key, state, attempts, hop_count, next_attempt_at, expires_at,
                lease_owner, lease_expires_at, purged_at, last_error, created_at, updated_at)
            VALUES (
                $queueId, $tenantId, $internalMessageId, $payloadReference, $mimeDigest,
                $direction, $mailFrom, $principal, $untrustedMessageId, $payloadBytes,
                $idempotencyKey, $state, 0, $hopCount, $nextAttemptAt, $expiresAt,
                NULL, NULL, NULL, NULL, $now, $now);
            """;

        cmd.Parameters.AddWithValue("$queueId", queueId);
        cmd.Parameters.AddWithValue("$tenantId", submission.TenantId);
        cmd.Parameters.AddWithValue("$internalMessageId", submission.InternalMessageId);
        cmd.Parameters.AddWithValue("$payloadReference", payloadReference);
        cmd.Parameters.AddWithValue("$mimeDigest", submission.MimeDigest);
        cmd.Parameters.AddWithValue("$direction", (int)submission.Direction);
        cmd.Parameters.AddWithValue("$mailFrom", submission.MailFrom);
        cmd.Parameters.AddWithValue("$principal", submission.TrustedPrincipalId);
        cmd.Parameters.AddWithValue(
            "$untrustedMessageId", (object?)submission.UntrustedMessageIdHeader ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payloadBytes", submission.Payload.Length);
        cmd.Parameters.AddWithValue(
            "$idempotencyKey", (object?)submission.IdempotencyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$hopCount", (object?)submission.HopCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$nextAttemptAt", nextAttemptAt is null ? DBNull.Value : ToDb(nextAttemptAt.Value));
        cmd.Parameters.AddWithValue("$expiresAt", ToDb(expiresAt));
        cmd.Parameters.AddWithValue("$now", ToDb(now));

        cmd.ExecuteNonQuery();
    }

    private static void InsertRecipients(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueId,
        IReadOnlyList<QueueRecipientState> recipients)
    {
        foreach (var recipient in recipients)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                """
                INSERT INTO queue_recipient (
                    queue_id, recipient_key, state, attempts, last_attempt_at, delivered_at,
                    next_attempt_at, re_evaluate_by, hold_surfaced_at, last_error)
                VALUES (
                    $queueId, $recipient, $state, 0, NULL, NULL,
                    $nextAttemptAt, $reEvaluateBy, NULL, NULL);
                """;

            cmd.Parameters.AddWithValue("$queueId", queueId);
            cmd.Parameters.AddWithValue("$recipient", recipient.Recipient);
            cmd.Parameters.AddWithValue("$state", (int)recipient.State);
            cmd.Parameters.AddWithValue(
                "$nextAttemptAt", recipient.NextAttemptAt is null ? DBNull.Value : ToDb(recipient.NextAttemptAt.Value));
            cmd.Parameters.AddWithValue(
                "$reEvaluateBy", recipient.ReEvaluateBy is null ? DBNull.Value : ToDb(recipient.ReEvaluateBy.Value));

            cmd.ExecuteNonQuery();
        }
    }

    private static int AppendAttempts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueId,
        DeliveryReport report,
        DateTimeOffset now)
    {
        var recorded = 0;

        foreach (var result in report.Recipients)
        {
            AppendAttempt(
                connection, transaction, queueId, result.Recipient, report.WorkerId,
                result.Outcome, result.Detail ?? report.Detail, now);

            recorded++;
        }

        return recorded;
    }

    private static void AppendAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueId,
        string recipientKey,
        string workerId,
        DeliveryAttemptOutcome outcome,
        string? detail,
        DateTimeOffset now)
    {
        // INSERT only. History is never updated and never deleted, that is what makes it possible
        // to reconstruct what was actually tried when a delivery is later disputed.
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            INSERT INTO queue_attempt (queue_id, recipient_key, worker_id, outcome, detail, attempted_at)
            VALUES ($queueId, $recipient, $worker, $outcome, $detail, $now);
            """;

        cmd.Parameters.AddWithValue("$queueId", queueId);
        cmd.Parameters.AddWithValue("$recipient", recipientKey);
        cmd.Parameters.AddWithValue("$worker", workerId);
        cmd.Parameters.AddWithValue("$outcome", outcome.ToString());
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", ToDb(now));

        cmd.ExecuteNonQuery();
    }

    private static void UpdateRecipient(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueId,
        string recipient,
        DeliveryState state,
        DateTimeOffset now,
        int attempts,
        DateTimeOffset? nextAttemptAt,
        string? lastError,
        DateTimeOffset? deliveredAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            UPDATE queue_recipient
               SET state = $state,
                   attempts = $attempts,
                   last_attempt_at = $now,
                   next_attempt_at = $nextAttemptAt,
                   delivered_at = $deliveredAt,
                   last_error = $lastError
             WHERE queue_id = $queueId AND recipient_key = $recipient;
            """;

        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.Parameters.AddWithValue("$now", ToDb(now));
        cmd.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt is null ? DBNull.Value : ToDb(nextAttemptAt.Value));
        cmd.Parameters.AddWithValue("$deliveredAt", deliveredAt is null ? DBNull.Value : ToDb(deliveredAt.Value));
        cmd.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$queueId", queueId);
        cmd.Parameters.AddWithValue("$recipient", recipient);

        cmd.ExecuteNonQuery();
    }

    private static void BumpItemAttempts(
        SqliteConnection connection, SqliteTransaction transaction, string queueId, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE queue_item SET attempts = attempts + $delta WHERE queue_id = $queueId;";
        cmd.Parameters.AddWithValue("$delta", delta);
        cmd.Parameters.AddWithValue("$queueId", queueId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Writes the item-level roll-up of its per-recipient states.</summary>
    private static void WriteItemScheduling(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string queueId,
        DeliveryState state,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset now,
        string? lastError)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            UPDATE queue_item
               SET state = $state,
                   next_attempt_at = $nextAttemptAt,
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   updated_at = $now,
                   last_error = COALESCE($lastError, last_error)
             WHERE queue_id = $queueId;
            """;

        cmd.Parameters.AddWithValue("$state", (int)state);
        cmd.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt is null ? DBNull.Value : ToDb(nextAttemptAt.Value));
        cmd.Parameters.AddWithValue("$now", ToDb(now));
        cmd.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$queueId", queueId);
        cmd.ExecuteNonQuery();
    }

    private static void SetItemTerminal(
        SqliteConnection connection, SqliteTransaction transaction, string queueId, DateTimeOffset now, string reason)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            UPDATE queue_item
               SET state = $state,
                   next_attempt_at = NULL,
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   updated_at = $now,
                   last_error = $reason
             WHERE queue_id = $queueId;
            """;

        cmd.Parameters.AddWithValue("$state", (int)DeliveryState.TerminalFailure);
        cmd.Parameters.AddWithValue("$now", ToDb(now));
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$queueId", queueId);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- mapping

    private readonly record struct ItemHeader(
        string TenantId,
        DeliveryState State,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAt,
        DateTimeOffset? ExpiresAt,
        int Attempts);

    private static ItemHeader? ReadItemHeader(
        SqliteConnection connection, SqliteTransaction? transaction, string queueId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT state, lease_owner, lease_expires_at, expires_at, attempts, tenant_id
              FROM queue_item
             WHERE queue_id = $queueId;
            """;
        cmd.Parameters.AddWithValue("$queueId", queueId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ItemHeader(
            reader.GetString(5),
            (DeliveryState)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : FromDb(reader.GetString(2)),
            reader.IsDBNull(3) ? null : FromDb(reader.GetString(3)),
            reader.GetInt32(4));
    }

    private static QueueItem? ReadItem(
        SqliteConnection connection, SqliteTransaction? transaction, string queueId, DateTimeOffset now)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT queue_id, tenant_id, internal_message_id, payload_reference, mime_digest,
                   direction, mail_from, trusted_principal_id, payload_bytes, state, attempts,
                   hop_count, next_attempt_at, expires_at, lease_owner, lease_expires_at,
                   purged_at, last_error, created_at, updated_at, untrusted_message_id
              FROM queue_item
             WHERE queue_id = $queueId;
            """;
        cmd.Parameters.AddWithValue("$queueId", queueId);

        string tenantId;
        string internalMessageId;
        string payloadReference;
        string mimeDigest;
        MailDirection direction;
        string mailFrom;
        string principal;
        long payloadBytes;
        DeliveryState state;
        int attempts;
        int? hopCount;
        DateTimeOffset? nextAttemptAt;
        DateTimeOffset? expiresAt;
        string? leaseOwner;
        DateTimeOffset? leaseExpiresAt;
        DateTimeOffset? purgedAt;
        string? lastError;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        string? untrustedMessageId;

        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            tenantId = reader.GetString(1);
            internalMessageId = reader.GetString(2);
            payloadReference = reader.GetString(3);
            mimeDigest = reader.GetString(4);
            direction = (MailDirection)reader.GetInt32(5);
            mailFrom = reader.GetString(6);
            principal = reader.GetString(7);
            payloadBytes = reader.GetInt64(8);
            state = (DeliveryState)reader.GetInt32(9);
            attempts = reader.GetInt32(10);
            hopCount = reader.IsDBNull(11) ? null : reader.GetInt32(11);
            nextAttemptAt = reader.IsDBNull(12) ? null : FromDb(reader.GetString(12));
            expiresAt = reader.IsDBNull(13) ? null : FromDb(reader.GetString(13));
            leaseOwner = reader.IsDBNull(14) ? null : reader.GetString(14);
            leaseExpiresAt = reader.IsDBNull(15) ? null : FromDb(reader.GetString(15));
            purgedAt = reader.IsDBNull(16) ? null : FromDb(reader.GetString(16));
            lastError = reader.IsDBNull(17) ? null : reader.GetString(17);
            createdAt = FromDb(reader.GetString(18)) ?? now;
            updatedAt = FromDb(reader.GetString(19)) ?? now;
            untrustedMessageId = reader.IsDBNull(20) ? null : reader.GetString(20);
        }

        var recipients = ReadRecipients(connection, transaction, queueId);

        return new QueueItem
        {
            QueueId = queueId,
            TenantId = tenantId,
            Envelope = new MailEnvelope
            {
                InternalMessageId = internalMessageId,
                TenantId = tenantId,
                Direction = direction,
                TrustedPrincipalId = principal,
                MailFrom = mailFrom,
                RcptTo = [.. recipients.Select(r => r.Recipient)],
                ReceivedAt = createdAt,
                MimeDigest = mimeDigest,
                PayloadReference = payloadReference,

                // Carried through as the untrusted value it is: available for diagnostics and loop
                // tracing, and never treated as a key.
                UntrustedMessageIdHeader = untrustedMessageId,
            },
            State = state,
            Attempts = attempts,
            HopCount = hopCount,
            PayloadBytes = payloadBytes,
            NextAttemptAt = nextAttemptAt,
            ExpiresAt = expiresAt,
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt,
            LastError = lastError,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            PurgedAt = purgedAt,
            Recipients = recipients,
        };
    }

    private static List<QueueRecipientState> ReadRecipients(
        SqliteConnection connection, SqliteTransaction? transaction, string queueId)
    {
        var recipients = new List<QueueRecipientState>();

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT recipient_key, state, attempts, last_attempt_at, delivered_at,
                   next_attempt_at, re_evaluate_by, last_error
              FROM queue_recipient
             WHERE queue_id = $queueId
             ORDER BY recipient_key;
            """;
        cmd.Parameters.AddWithValue("$queueId", queueId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            recipients.Add(new QueueRecipientState
            {
                Recipient = reader.GetString(0),
                State = (DeliveryState)reader.GetInt32(1),
                Attempts = reader.GetInt32(2),
                LastAttemptAt = reader.IsDBNull(3) ? null : FromDb(reader.GetString(3)),
                DeliveredAt = reader.IsDBNull(4) ? null : FromDb(reader.GetString(4)),
                NextAttemptAt = reader.IsDBNull(5) ? null : FromDb(reader.GetString(5)),
                ReEvaluateBy = reader.IsDBNull(6) ? null : FromDb(reader.GetString(6)),
                LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }

        return recipients;
    }

    /// <summary>
    /// Derives the item-level state and next due time from its recipients.
    /// </summary>
    /// <remarks>
    /// The item is claimable when its <em>earliest</em> unsettled recipient comes due, so a worker
    /// wakes for the recipient that is ready rather than sleeping until the slowest one is. The
    /// claim then hands the worker only the recipients that are actually due.
    /// </remarks>
    private static (DeliveryState State, DateTimeOffset? NextAttemptAt) RollUp(
        IReadOnlyList<QueueRecipientState> recipients, DateTimeOffset now)
    {
        if (recipients.Count > 0 && recipients.All(r => r.State == DeliveryState.Delivered))
        {
            return (DeliveryState.Delivered, null);
        }

        var scheduled = recipients
            .Where(r => r.State is DeliveryState.Queued or DeliveryState.RetryScheduled)
            .ToList();

        if (scheduled.Count > 0)
        {
            var earliest = scheduled.Min(r => r.NextAttemptAt ?? now);
            return (earliest > now ? DeliveryState.RetryScheduled : DeliveryState.Queued, earliest);
        }

        var awaitingDecision = recipients
            .Where(r => r.State is DeliveryState.Held or DeliveryState.Quarantined)
            .ToList();

        if (awaitingDecision.Count > 0)
        {
            return (
                awaitingDecision.Any(r => r.State == DeliveryState.Quarantined)
                    ? DeliveryState.Quarantined
                    : DeliveryState.Held,
                null);
        }

        // Nothing left to attempt and not everything was delivered. The per-recipient rows carry
        // which ones made it; this state means only "the queue is done with this message".
        return (DeliveryState.TerminalFailure, null);
    }

    /// <summary>Builds the initial per-recipient rows, applying the default hold window.</summary>
    private List<QueueRecipientState> BuildRecipients(QueueSubmission submission, DateTimeOffset now)
        => [.. submission.Recipients.Select(admission => new QueueRecipientState
        {
            Recipient = admission.Recipient,
            State = admission.State,
            Attempts = 0,
            NextAttemptAt = admission.State == DeliveryState.Queued ? now : null,
            ReEvaluateBy = admission.State == DeliveryState.Held
                ? admission.ReEvaluateBy ?? now + _options.DefaultHoldWindow
                : null,
        })];

    private async Task<SubmissionLookup?> FindByIdempotencyKeyAsync(
        string tenantId, string key, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT queue_id, mime_digest FROM queue_item
             WHERE tenant_id = $tenant AND idempotency_key = $key;
            """;
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        cmd.Parameters.AddWithValue("$key", key);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SubmissionLookup
            {
                QueueId = reader.GetString(0),
                MimeDigest = reader.GetString(1),
                IdempotencyKey = key,
            }
            : null;
    }

    private static (long Items, long Bytes) ReadTenantUsage(
        SqliteConnection connection, SqliteTransaction transaction, string tenantId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM queue_item
                  WHERE tenant_id = $tenant AND state NOT IN ($delivered, $terminal)),
                (SELECT COALESCE(SUM(payload_bytes), 0) FROM queue_item
                  WHERE tenant_id = $tenant AND purged_at IS NULL);
            """;

        cmd.Parameters.AddWithValue("$tenant", tenantId);
        cmd.Parameters.AddWithValue("$delivered", (int)DeliveryState.Delivered);
        cmd.Parameters.AddWithValue("$terminal", (int)DeliveryState.TerminalFailure);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = _connections.Open();

        using var pragma = connection.CreateCommand();

        // synchronous=FULL, not the NORMAL the profile store uses. For profiles, a commit lost to a
        // power cut costs a rebuildable snapshot; for the queue, the commit *is* the moment delivery
        // responsibility transfers. A 250 returned against a commit that a power loss can discard
        // is mail we promised to deliver and then forgot.
        var busyTimeoutMs = (long)_options.BusyTimeout.TotalMilliseconds;
        pragma.CommandText =
            $"PRAGMA synchronous = FULL; PRAGMA busy_timeout = {busyTimeoutMs.ToString(CultureInfo.InvariantCulture)};";
        pragma.ExecuteNonQuery();

        return connection;
    }

    // ---------------------------------------------------------------- validation

    private static void ValidateSubmission(QueueSubmission submission)
    {
        Require(submission.TenantId, nameof(submission.TenantId));
        Require(submission.InternalMessageId, nameof(submission.InternalMessageId));
        Require(submission.TrustedPrincipalId, nameof(submission.TrustedPrincipalId));
        // The null sender, and it is DIRECTION-DEPENDENT, which an earlier version of this check got
        // wrong in the most damaging direction.
        //
        // `Require(submission.MailFrom)` used to run here unconditionally. That threw on "" for
        // *both* directions, and a DSN being delivered to one of our users is ordinary, legitimate
        // mail, so a routine case was refused outright. That hole predates the null-sender check
        // below; the check only made its absence look intentional.
        //
        // `MailEnvelope.MailFrom` is "" for a null sender. `<>` is wire notation, normalised at the
        // parse boundary, and is accepted here too so a caller that passes the wire form is not
        // silently misread as having a real address.
        // A null sender is not a construction error, it is legitimate on inbound, so `MailFrom` is
        // only required to hold a real address when it is *not* one. The direction-dependent refusal
        // lives in AcceptAsync with the other policy refusals, because it returns rather than throws.
        if (!SenderAddresses.IsNullSender(submission.MailFrom))
        {
            Require(submission.MailFrom, nameof(submission.MailFrom));
        }
        Require(submission.MimeDigest, nameof(submission.MimeDigest));

        if (submission.Recipients.Count == 0)
        {
            throw new ArgumentException(
                "A submission must name at least one recipient. Delivery responsibility is " +
                "per recipient, so a message with none is not a message we can accept.",
                nameof(submission));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var admission in submission.Recipients)
        {
            Require(admission.Recipient, "Recipients[].Recipient");

            if (!seen.Add(admission.Recipient))
            {
                throw new ArgumentException(
                    $"Recipient '{admission.Recipient}' appears more than once. Deduplicating silently " +
                    "would mean delivering to fewer recipients than the transaction named.",
                    nameof(submission));
            }

            if (admission.State is DeliveryState.Delivered
                or DeliveryState.TerminalFailure
                or DeliveryState.Delivering
                or DeliveryState.RetryScheduled)
            {
                throw new ArgumentException(
                    $"Recipient '{admission.Recipient}' cannot be admitted in state {admission.State}; " +
                    "a submission may only start as Queued, Held or Quarantined.",
                    nameof(submission));
            }
        }

        if (submission.HopCount is < 0)
        {
            throw new ArgumentException("Hop count cannot be negative.", nameof(submission));
        }
    }

    private static void ValidateReport(DeliveryReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(report.WorkerId);

        foreach (var result in report.Recipients)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(result.Recipient);

            // The permitted set lives with the port contract so the documented vocabulary and the
            // enforced one cannot drift apart. A test asserts they agree for every enum value.
            if (!DeliveryPortContract.ReportableOutcomes.Contains(result.Outcome))
            {
                throw new ArgumentException(
                    $"Outcome {result.Outcome} is produced by the queue itself and cannot be reported " +
                    "by a worker. A worker reporting a policy event would make an elapsed timer or a " +
                    $"reviewer's decision look like something we observed on the wire. A port may " +
                    $"report: {string.Join(", ", DeliveryPortContract.ReportableOutcomes)}.",
                    nameof(report));
            }
        }
    }


    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
    }

    // ---------------------------------------------------------------- misc

    private static string NewQueueId() => $"q-{Guid.NewGuid():N}";

    /// <summary>
    /// Renders an instant for storage as a fixed-width UTC string.
    /// </summary>
    /// <remarks>
    /// Everything is normalised to UTC before formatting. The queue compares timestamps as strings
    /// in SQL, which is only correct while every stored value shares one format and one offset,     /// a stray <c>+01:00</c> would sort into the wrong place and silently reorder a lease or an
    /// expiry.
    /// </remarks>
    private static string ToDb(DateTimeOffset value)
        => value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? FromDb(string? value)
        => value is null
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

}

/// <summary>
/// Raised when the queue's metadata and its spool disagree in the dangerous direction: a message
/// we accepted whose payload is gone.
/// </summary>
/// <remarks>
/// This should be unreachable, payload-before-metadata ordering exists precisely to prevent it, /// so it is a signal that the durability contract has been violated or the spool was altered. It
/// is never retryable and never silently repaired: the bytes it names are mail we took
/// responsibility for and cannot recover, and only a human can decide what to do about that.
/// </remarks>
public sealed class QueueIntegrityException : Exception
{
    public QueueIntegrityException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

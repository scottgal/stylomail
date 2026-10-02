"""Mutation set for the `queue-` lane.

Each entry is (name, file, old_text, new_text, claims_test):
  * `old_text` must appear in the file EXACTLY ONCE, or the entry is INVALID.
  * `new_text` must change behaviour. A no-op replacement is INVALID, never a verdict.
  * `claims_test` (optional) names the test whose *name* asserts this behaviour. The harness then
    distinguishes CLAIMED (that test went red) from ELSEWHERE (some other test did, so the claim
    is not actually verified), see the header of mutate.py.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "src/StyloMail.Queue"

PROJECT = "tests/StyloMail.Queue.Tests/StyloMail.Queue.Tests.csproj"

MUTATIONS = [
    ("A: .tmp leftover sweep removed",
     SRC / "SpoolStore.cs",
     '''            _root, $"*{TemporarySuffix}", SearchOption.AllDirectories)''',
     '''            _root, $"*.never", SearchOption.AllDirectories)''',
     "A_temporary_abandoned_by_a_crashed_acceptance_is_swept"),

    ("B: byte-quota refusal returns the ITEM-limit code",
     SRC / "QueueStore.cs",
     """                QueueAdmission.RefusedTenantByteLimit,""",
     """                QueueAdmission.RefusedTenantItemLimit,""",
     "One_tenant_cannot_exhaust_the_spool"),

    ("C: hold never marked surfaced",
     SRC / "QueueStore.cs",
     """                   SET hold_surfaced_at = $now
                 WHERE queue_id = $queueId AND recipient_key = $recipient;""",
     """                   SET hold_surfaced_at = hold_surfaced_at
                 WHERE queue_id = $queueId AND recipient_key = $recipient;""",
     "An_expired_hold_is_surfaced_as_a_policy_decision_and_not_an_acknowledgement"),

    ("D: PermanentFailure retries instead of terminating",
     SRC / "QueueStore.cs",
     """            case DeliveryAttemptOutcome.PermanentFailure:
            case DeliveryAttemptOutcome.HopLimitExceeded:
                UpdateRecipient(
                    connection, transaction, queueId, recipient.Recipient,
                    DeliveryState.TerminalFailure, now, recipient.Attempts + 1, nextAttemptAt: null,
                    lastError: result.Detail, deliveredAt: null);
                break;""",
     """            case DeliveryAttemptOutcome.PermanentFailure:
            case DeliveryAttemptOutcome.HopLimitExceeded:
                UpdateRecipient(
                    connection, transaction, queueId, recipient.Recipient,
                    DeliveryState.RetryScheduled, now, recipient.Attempts + 1,
                    nextAttemptAt: now + TimeSpan.FromMinutes(1),
                    lastError: result.Detail, deliveredAt: null);
                break;""",
     "A_multi_recipient_message_retries_only_the_recipients_still_pending"),

    ("E: expiry sweep disabled entirely",
     SRC / "QueueStore.cs",
     """            ExpireOverdueItems(connection, transaction, tenantId, now, expired);""",
     """            _ = expired;""",
     "A_message_is_given_up_on_at_its_configured_expiry"),

    ("F: quarantine release records no audit row",
     SRC / "QueueStore.cs",
     """            AppendAttempt(
                connection, transaction, queueId, recipient.Recipient, decidedBy,
                resolution == QuarantineResolution.Release
                    ? DeliveryAttemptOutcome.QuarantineReleased
                    : DeliveryAttemptOutcome.QuarantineRejected,""",
     """            AppendAttempt(
                connection, transaction, queueId, "*", "noop",
                resolution == QuarantineResolution.Release
                    ? DeliveryAttemptOutcome.HoldExpired
                    : DeliveryAttemptOutcome.HoldExpired,""",
     "A_quarantined_message_is_released_for_delivery_by_a_reviewer"),

    ("G: expired hold auto-releases to Delivered",
     SRC / "QueueStore.cs",
     """                   SET hold_surfaced_at = $now
                 WHERE queue_id = $queueId AND recipient_key = $recipient;""",
     """                   SET hold_surfaced_at = $now, state = 4
                 WHERE queue_id = $queueId AND recipient_key = $recipient;""",
     "An_expired_hold_is_surfaced_as_a_policy_decision_and_not_an_acknowledgement"),

    ("H: spool failure swallowed, row committed against a missing payload",
     SRC / "QueueStore.cs",
     """        var payloadReference = PayloadReferences.RequireDurable(await _spool
            .WriteAsync(submission.TenantId, queueId, submission.Payload, cancellationToken)
            .ConfigureAwait(false));""",
     """        string payloadReference;
        try
        {
            payloadReference = await _spool
                .WriteAsync(submission.TenantId, queueId, submission.Payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SpoolUnavailableException)
        {
            payloadReference = "spool://acme/never-written.eml";
        }""",
     "Acceptance_is_refused_when_the_spool_cannot_write"),

    ("I: claim does not take the lease",
     SRC / "QueueStore.cs",
     """                UPDATE queue_item
                   SET state = $delivering,
                       lease_owner = $worker,""",
     """                UPDATE queue_item
                   SET lease_owner = $worker,""",
     "Concurrent_claims_never_hand_one_message_to_two_workers"),

    ("J: oversize payload no longer refused before spooling",
     SRC / "QueueStore.cs",
     """        if (submission.Payload.Length > _options.MaxPayloadBytes)
        {
            return QueueAcceptResult.Refused(
                QueueAdmission.RefusedPayloadTooLarge,
                $"Payload is {submission.Payload.Length} bytes, over the {_options.MaxPayloadBytes} limit.");
        }

""",
     "",
     "Payloads_over_the_size_limit_are_refused_before_the_spool_is_touched"),

    ("K: retry exhaustion reports the expiry reason",
     SRC / "QueueStore.cs",
     """            var reason = exhausted
                ? $"Gave up after {attempts} attempts (limit {_options.MaxAttemptsPerRecipient})."
                : $"No retry would fall inside the message lifetime ending {expiresAt:O}.";""",
     """            _ = exhausted;
            var reason = $"No retry would fall inside the message lifetime ending {expiresAt:O}.";""",
     "Retries_are_bounded_and_the_recipient_ends_in_terminal_failure"),

    # A mutation that removes a method's only use of instance state stops compiling (CA1822 is an
    # error here). The harness reports that as INCONCLUSIVE rather than a verdict, so mutations
    # must be chosen so they still compile.
    ("L: the port vocabulary check accepts anything",
     SRC / "QueueStore.cs",
     """            if (!DeliveryPortContract.ReportableOutcomes.Contains(result.Outcome))""",
     """            if (false)""",
     "The_documented_port_outcomes_are_exactly_the_ones_the_store_accepts"),

    ("M: listings never page (cursor is always null)",
     SRC / "QueueStore.Listing.cs",
     """        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);""",
     """        if (false)
        {
            rows.RemoveAt(rows.Count - 1);""",
     "Paging_visits_every_item_exactly_once"),

    ("N: listing ignores the tenant filter",
     SRC / "QueueStore.Listing.cs",
     """                 WHERE i.tenant_id = $tenant""",
     """                 WHERE ($tenant IS NOT NULL)""",
     "A_listing_is_scoped_to_its_tenant"),

    ("O: pre-DDL collision check removed",
     SRC / "QueueSchema.cs",
     """        VerifyShape(connection, transaction, requireAllTables: false);

""",
     "",
     "A_colliding_table_of_the_wrong_shape_is_detected_rather_than_adopted"),

    ("P: queue_recipient declares no foreign key",
     SRC / "QueueSchema.cs",
     """            PRIMARY KEY (queue_id, recipient_key),
            FOREIGN KEY (queue_id) REFERENCES queue_item (queue_id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_queue_recipient_pending""",
     """            PRIMARY KEY (queue_id, recipient_key)
        );

        CREATE INDEX IF NOT EXISTS ix_queue_recipient_pending""",
     "Foreign_keys_are_enforced_on_the_connections_the_store_uses"),

    # Retargeted when the drain window moved from CancelAfter (wall clock) to a TimeProvider-scheduled
    # timer. The failure mode is unchanged: cancel on shutdown instead of granting the window.
    ("Q: shutdown abandons in-flight delivery instead of draining",
     SRC / "QueueDeliveryWorker.cs",
     # RE-ANCHORED: the drain-timer lifetime fix moved the timer's creation out of the shutdown
     # callback and into RunAsync's prologue, so the old anchor (the CreateTimer call inside the
     # callback) no longer exists and this came back INVALID.
     #
     # The first re-anchor armed the timer to ZERO and came back ELSEWHERE, and cancelling inline had
     # its own problem. The mutation was right; the CLAIMING TEST could not see it reliably. The port
     # signalled `entered` before it registered `gate.Task.WaitAsync(ct)`, so when the test's
     # continuation ran first it cancelled and released the gate before the wait existed, and
     # `WaitAsync` then returned the ALREADY-COMPLETED gate and ignored the cancellation entirely.
     # Measured on a mutated copy built once and run with --no-build: 2 of 5 full-suite runs GREEN,
     # and the harness itself gave GAP on one run and CLAIMED on another, same tree. A guard that
     # passes an implementation abandoning the delivery 40% of the time is a coin flip on the exact
     # behaviour its name claims.
     #
     # Fixed in the TEST, not by weakening the mutation: the port registers the wait first and signals
     # `entered` after. Pristine source is green 6/6, this mutation red 6/6.
     # Dropping `static` is required to capture `inFlight` (CS8820 otherwise); the callback is still
     # disposed with the registration, so this changes when the delivery ends, not the timer's
     # lifetime.
     """            static state =>
            {
                var (timer, drainTimeout) = ((ITimer Timer, TimeSpan DrainTimeout))state!;""",
     """            state =>
            {
                inFlight.Cancel();
                var (timer, drainTimeout) = ((ITimer Timer, TimeSpan DrainTimeout))state!;""",
     "Shutdown_drains_an_in_flight_delivery_instead_of_abandoning_it"),

    ("R: a port fault is recorded as Delivered",
     SRC / "QueueDeliveryWorker.cs",
     """                    Recipient = recipient.Recipient,
                    Outcome = DeliveryAttemptOutcome.TemporaryFailure,""",
     """                    Recipient = recipient.Recipient,
                    Outcome = DeliveryAttemptOutcome.Delivered,""",
     "A_port_that_throws_is_recorded_as_temporary_and_never_as_silence"),

    ("S: worker dispatches every recipient, not just the pending ones",
     SRC / "QueueDeliveryWorker.cs",
     """            Recipients = [.. lease.PendingRecipients.Select(r => r.Recipient)],""",
     """            Recipients = [.. lease.Item.Recipients.Select(r => r.Recipient)],""",
     "The_port_is_given_the_original_bytes_and_exactly_the_pending_recipients"),

    ("T: a missing payload yields a substitute instead of an integrity fault",
     SRC / "QueueStore.cs",
     """            ?? throw new QueueIntegrityException(
                $"Queue item {item.QueueId} has metadata but its payload at " +
                $"'{item.Envelope.PayloadReference}' is missing. Payload-before-metadata ordering makes " +
                "this unreachable by design, so the durability contract has been violated or the spool " +
                "was altered. The message cannot be delivered and must not be silently discarded.");""",
     """            ?? new MemoryStream(1);""",
     "A_message_whose_payload_vanished_is_left_alone_rather_than_settled"),

    ("U: listing limit is not clamped",
     SRC / "QueueStore.Listing.cs",
     """        var limit = Math.Clamp(query.Limit, 1, QueueListingLimits.MaxPageSize);""",
     """        var limit = query.Limit;""",
     "A_page_is_clamped_to_the_hard_ceiling_rather_than_rejected"),

    ("V: spool budget excludes terminal payloads still on disk",
     SRC / "QueueStore.cs",
     """                (SELECT COALESCE(SUM(payload_bytes), 0) FROM queue_item
                  WHERE tenant_id = $tenant AND purged_at IS NULL);""",
     """                (SELECT COALESCE(SUM(payload_bytes), 0) FROM queue_item
                  WHERE tenant_id = $tenant AND purged_at IS NULL
                    AND state NOT IN ($delivered, $terminal));""",
     "A_delivered_but_unpurged_payload_still_occupies_the_spool_budget"),

    ("W: the drain window never closes",
     SRC / "QueueDeliveryWorker.cs",
     # RE-ANCHORED for the same reason as Q. Same anchor text, opposite destruction: arming the timer
     # to infinity means the window never closes, so a delivery that outlasts DrainTimeout is never
     # cut off and the worker leaves an in-flight delivery running with nothing to bound it.
     """                    timer.Change(drainTimeout, Timeout.InfiniteTimeSpan);""",
     """                    timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);""",
     "A_delivery_that_outlasts_the_drain_window_is_cut_off_and_left_unsettled"),

    ("X: a result arriving after cancellation is discarded",
     SRC / "QueueDeliveryWorker.cs",
     """        await _store.CompleteAsync(lease, portResult.AsReport(_workerOptions.WorkerId), CancellationToken.None)""",
     """        await _store.CompleteAsync(lease, portResult.AsReport(_workerOptions.WorkerId), cancellationToken)""",
     "A_result_returned_despite_cancellation_is_applied_not_discarded"),

    ("Y: paging cursor pairs the probe row's timestamp with the kept row's id",
     SRC / "QueueStore.Listing.cs",
     """            rows.RemoveAt(rows.Count - 1);
            nextCursor = EncodeCursor(FromDb(rows[^1].CreatedAt) ?? now, rows[^1].QueueId);""",
     """            var probe = rows[^1];
            rows.RemoveAt(rows.Count - 1);
            nextCursor = EncodeCursor(FromDb(probe.CreatedAt) ?? now, rows[^1].QueueId);""",
     "Paging_visits_every_item_exactly_once_with_distinct_timestamps"),

    # ADDED 2026-10-01. Every assertion on `DeliveredAt` in the repository used to be an
    # `Assert.Null` (two in this project, two in Transport's seam tests), so the column could have
    # stopped being written and the whole fleet would have stayed green while the operator UI showed
    # an empty delivered time. No mutation covered it either, so the sweep could not have caught it.
    # The claiming test now asserts the value positively against the harness clock, which is what
    # makes this mutation CLAIMED rather than a GAP.
    ("Z: the delivered timestamp is never written",
     SRC / "QueueStore.cs",
     """        cmd.Parameters.AddWithValue("$deliveredAt", deliveredAt is null ? DBNull.Value : ToDb(deliveredAt.Value));""",
     """        cmd.Parameters.AddWithValue("$deliveredAt", DBNull.Value);""",
     "A_worker_delivers_a_claimed_message_through_the_port"),
]
